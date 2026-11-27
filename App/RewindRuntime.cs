using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Actions;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Logging;
using GlobalRewind.Net;
using GlobalRewind.Players;
using GlobalRewind.Snapshot;
using GlobalRewind.Tree;
using GlobalRewind.Game;

namespace GlobalRewind.App;

/// <summary>
/// 运行时编排：什么时候抓快照、什么时候恢复、什么时候落盘。
///
/// 快照点只选"静止的决策点"，判据见 IsStableDecisionPoint：
///   · 战斗开始（CombatBegan，force，唯一允许非稳定点的根节点）
///   · 玩家回合开始（TurnStarted）
///   · 队列里所有 GameAction 都结算完（AfterFinished / BeforeCancelled 后 _actionDepth 归零）
///
/// 关键：动作队列瞬时清空**不等于**稳定点。单人局点结束回合后，EndPlayerTurnAction /
/// ReadyToBeginEnemyTurnAction 完成时队列是空的、CurrentSide 也还是 Player，但此时
/// Phase=End 且 PlayerActionsDisabled=true，只是"点击结束回合 → 直接进入下一回合"之间
/// 的中间过渡。以前在这里抓点会得到按钮处于撤销态、牌不能打的半截状态，回溯过去没有意义。
/// 现在统一由门禁挡掉，只保留 TurnStarted 抓出来的"第 N 回合"节点。
///
/// 事件记录不再用动作白名单：全部从 ActionQueueSet.ActionEnqueued 进入（含 hook），
/// 玩家选择另由 PlayerChoiceSynchronizer.PlayerChoiceReceived 记录，流水账见 rew_events。
///
/// 多人（M0/M1）：
///   · 动作深度按 OwnerId 计（每个玩家一条队列），IsBusy 额外看队列/执行器；
///   · 建点只在“全体活跃玩家 Phase=Play + 无人已结束回合 + 所有队列为空”的静止点；
///   · 多人动作 AfterFinished 时动作还没完全摘掉，抓点延迟到下一帧；
///   · 真联机 JumpTo 直接拒绝，协同协议留到 M2；假多人（单进程 dummy）允许本地跳。
/// </summary>
internal sealed class RewindRuntime
{
    public static RewindRuntime Instance { get; } = new RewindRuntime();

    private readonly RewindTree _tree = new RewindTree();
    private readonly RewindStore _store = new RewindStore();
    private readonly CombatEventLog _events = new CombatEventLog();
    private readonly Dictionary<GameAction, string> _pendingLabels =
        new Dictionary<GameAction, string>(ReferenceEqualityComparer.Instance);

    // 已挂事件的同步器实例。RunManager 在每局 InitializeShared 里重建这一组对象，
    // 因此按引用比对做幂等订阅，避免每场战斗重复挂、或退订不到上一局的旧对象。
    private ActionQueueSet _subQueueSet;
    private PlayerChoiceSynchronizer _subChoiceSynchronizer;
    private ChecksumTracker _subChecksumTracker;
    private bool _subscribed;

    // 多人局里每个玩家有独立的动作队列，一个全局 depth 会把“A 结算完、B 还在跑”误判成空闲。
    // 按 OwnerId 计数，只有所有 owner 都归零才算真正结算完。
    private readonly Dictionary<ulong, int> _actionDepthByOwner = new Dictionary<ulong, int>();
    private int _combatIndex;
    private bool _combatActive;
    private bool _suppressCapture;
    private string _lastQuiescenceReason;

    // 多人局动作的 AfterFinished 触发时，动作还没从队列/执行器里摘干净；
    // 这时直接抓点会被静止探针挡住。推迟到下一帧（动作完全落地后）再试。
    private string _pendingCaptureLabel;
    private string _pendingCaptureKind;

    // 动作边界抓点用：记住最近一个“玩家根动作”（出牌/药水/结束回合），
    // 这样当它派生出的 hook 最后结算时，节点标签仍然写“打出 X”而不是 hook 的名字。
    private GameAction _lastPlayerAction;
    private string _lastPlayerActionLabel;
    private string _lastPlayerActionKind;

    /// <summary>
    /// 逻辑层状态发生变化（抓点 / 跳转 / 剪枝 / 回合推进）。
    /// 只发事件，不直接碰 UI —— UI 的刷新由 App 层的 RewindUiSource 转发。
    /// </summary>
    public event Action Changed;

    /// <summary>本场战斗或整局结束，UI 应收起（具体由 App 层转发）。</summary>
    public event Action SessionEnded;

    public RewindTree Tree => _tree;

    /// <summary>本局全部战斗事件的流水账，供 rew_events 查询。</summary>
    public CombatEventLog Events => _events;

    public bool IsCombatActive => _combatActive;

    /// <summary>
    /// 我们自己跟踪到的“还有动作没结算完”。用于 SettleAction 内部判断：
    /// AfterFinished 触发时动作还没从队列里摘掉，不能在这里看队列/执行器，
    /// 否则每个动作结算完都会被误判成“还在忙”，永远抓不到点。
    /// </summary>
    private bool HasTrackedActions
    {
        get
        {
            foreach (int depth in _actionDepthByOwner.Values)
            {
                if (depth > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool IsBusy
    {
        get
        {
            // 协同跳转期间（冻结/写回/等对端）一律按“忙”处理：
            // 这时本地既不该抓点，也不该再接受一次新的跳转。
            if (RewindCoordinator.Instance.IsBusy)
            {
                return true;
            }

            if (HasTrackedActions)
            {
                return true;
            }

            // 队列里还有动作（哪怕还没开始执行）也算忙：多人局里 A 的动作可能在
            // 等 B 的选牌，depth 计数不一定能覆盖所有入队路径。
            try
            {
                RunManager run = RunManager.Instance;
                if (run?.ActionQueueSet != null && !run.ActionQueueSet.IsEmpty)
                {
                    return true;
                }

                if (run?.ActionExecutor?.CurrentlyRunningAction != null)
                {
                    return true;
                }
            }
            catch
            {
                // 读不到就退回 depth 判断
            }

            return false;
        }
    }

    /// <summary>当前是否处于可以抓快照的“稳定决策点”（回合过渡中返回 false）。</summary>
    public bool IsStable
    {
        get
        {
            if (PlayerScope.IsMultiplayerLike)
            {
                return QuiescenceProbe.TryCapture(out _, out _);
            }

            return IsStableDecisionPoint(out _);
        }
    }

    /// <summary>最近一次抓点被门禁跳过的原因；成功抓点后清空。供 rew_status / rew_capture 回显。</summary>
    public string LastCaptureSkipReason { get; private set; }

    /// <summary>最近一次“全体静止点”探测失败的原因（多人局诊断用）。</summary>
    public string LastQuiescenceReason => _lastQuiescenceReason;

    /// <summary>协同跳转正在等待对端（M2）。单机局恒为 false。</summary>
    public bool IsJumpPending => RewindCoordinator.Instance.LastResult?.Pending ?? false;

    /// <summary>最近一次协同跳转的请求号；单机局为 0。</summary>
    public int LastJumpRequestId => RewindCoordinator.Instance.LastResult?.RequestId ?? 0;

    /// <summary>协同剪枝正在等待对端确认（M2+）。单机局恒为 false。</summary>
    public bool IsPrunePending => RewindCoordinator.Instance.LastPrune?.Pending ?? false;

    public bool IsMultiplayer => PlayerScope.IsMultiplayer;

    public bool IsMultiplayerLike => PlayerScope.IsMultiplayerLike;

    public bool IsFakeMultiplayer => PlayerScope.IsFakeMultiplayer;

    /// <summary>当前活状态的切面；多人局由它决定“两个节点是不是同一逻辑点”。</summary>
    public Frontier CurrentFrontier
    {
        get
        {
            try
            {
                return Frontier.Capture(PlayerScope.CombatState);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// 多人局用：动作 AfterFinished 时动作还没完全从队列/执行器摘掉，
    /// 先记下“动作 X 结算完”，等下一帧真正静止后再抓点。
    /// 由 <see cref="RewindInputInstaller"/> 的 ProcessFrame 驱动。
    /// </summary>
    public void PumpDeferredCapture()
    {
        if (!_combatActive || _suppressCapture || _pendingCaptureLabel == null)
        {
            return;
        }

        if (HasTrackedActions)
        {
            return;
        }

        // 探针失败就保持 pending，等下一个静止窗口；不要每帧写 capture_skipped。
        if (!QuiescenceProbe.TryCapture(out _, out string reason))
        {
            _lastQuiescenceReason = reason;
            return;
        }

        _lastQuiescenceReason = null;
        string label = _pendingCaptureLabel;
        string kind = _pendingCaptureKind;
        ClearPendingCapture();
        Capture(label, kind);
    }

    private void ClearPendingCapture()
    {
        _pendingCaptureLabel = null;
        _pendingCaptureKind = null;
    }

    public int CombatIndex => _combatIndex;

    /// <summary>最近一次合并产生的传送门 id（换主时是刚被降级的旧主），UI 用来高亮。</summary>
    public int LastPortalId { get; private set; }

    /// <summary>最近一次跳转后的逐字段核对结果（正常应为 0）。</summary>
    public int LastRestoreMismatches { get; private set; }

    public string LastRestoreReport { get; private set; } = "尚未跳转过";

    /// <summary>最近一次跳转的"深拷贝逐字节一致"判定。</summary>
    public bool LastRestoreDeepEqual { get; private set; }

    public string LastRestoreDeepHash { get; private set; }

    public string LastRestoreDeepDetail { get; private set; } = "尚未跳转过";

    public bool IsSingleplayer
    {
        get
        {
            RunManager manager = RunManager.Instance;
            return manager != null && manager.NetService != null && manager.NetService.Type == NetGameType.Singleplayer;
        }
    }

    // ---------------------------------------------------------------- 对 UI 的通知

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            ModLog.Warn($"ui change handler failed: {e.Message}");
        }
    }

    private void NotifySessionEnded()
    {
        try
        {
            SessionEnded?.Invoke();
        }
        catch (Exception e)
        {
            ModLog.Warn($"ui session handler failed: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 生命周期

    public void OnRunStarted()
    {
        try
        {
            _combatIndex = 0;
            _combatActive = false;
            _actionDepthByOwner.Clear();
            _pendingLabels.Clear();
            ClearPendingCapture();
            _tree.Clear();
            _events.Clear();

            string seed = RunManager.Instance?.DebugOnlyGetState()?.Rng?.StringSeed ?? string.Empty;
            _store.PrepareRun(seed);

            // ActionQueueSet / PlayerChoiceSynchronizer / ChecksumTracker 是每局
            // InitializeShared 里新建的，Init() 时还是 null，只能在 RunStarted 之后挂。
            EnsureSubscribed();

            // 网络处理器要在第一场战斗之前就位：握手消息可能比本端的 CombatBegan 更早到。
            RewindCoordinator.Instance.OnRunStarted();

            _events.Add("run_started", "run", $"seed={seed} net={PlayerScope.NetType}");
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnRunStarted failed: {e.Message}");
        }
    }

    public void OnCombatBegan(CombatState state)
    {
        try
        {
            NetGameType netType = PlayerScope.NetType;
            bool multiplayer = PlayerScope.IsMultiplayer;
            bool fakeMultiplayer = PlayerScope.IsFakeMultiplayer;
            int playerCount = PlayerScope.All.Count;
            ModLog.Info($"CombatBegan received (net={netType}, players={playerCount}, "
                        + $"mp={multiplayer}, fakeMp={fakeMultiplayer})");

            if (netType == NetGameType.None || netType == NetGameType.Replay)
            {
                ModLog.Warn($"CombatBegan skipped: net type {netType}");
                return;
            }

            _combatActive = true;
            _actionDepthByOwner.Clear();
            _pendingLabels.Clear();
            ClearPendingCapture();
            _lastPlayerAction = null;
            _lastPlayerActionLabel = null;
            _lastPlayerActionKind = null;
            _lastQuiescenceReason = null;
            _tree.Clear();
            _combatIndex++;

            // continue_run 载入的存档不一定再走一遍 RunStarted，这里补一次幂等订阅。
            EnsureSubscribed();
            RewindCoordinator.Instance.OnCombatBegan();

            string encounter = state?.Encounter?.Id.Entry ?? "unknown";
            _store.BeginCombat(_combatIndex, encounter);
            (int round0, int turn0, int node0) = Context();
            _events.Add("combat_start", "encounter",
                $"{encounter} net={netType} players={playerCount}", 0, round0, turn0, node0,
                actorId: PlayerScope.LocalNetId, stream: "system", frontier: FrontierText());
            Capture("战斗开始", "combat_start", force: true);

            ModLog.Info($"combat #{_combatIndex} ({encounter}) tree opened "
                        + $"(net={netType}, players={playerCount})");
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnCombatBegan failed: {e.Message}");
        }
    }

    public void OnTurnStarted(CombatState state)
    {
        try
        {
            if (!_combatActive || state == null)
            {
                return;
            }

            // 回合切换时清掉可能残留的动作计数（动作被取消时不会触发 AfterFinished）
            _actionDepthByOwner.Clear();
            _pendingLabels.Clear();
            ClearPendingCapture();
            _lastPlayerAction = null;
            _lastPlayerActionLabel = null;
            _lastPlayerActionKind = null;

            Player me0 = PlayerScope.Local;
            int turn0 = me0?.PlayerCombatState?.TurnNumber ?? 0;
            string players = string.Join(", ", state.Players.Select(player =>
                $"{player.NetId}:{player.PlayerCombatState?.Phase}/{player.PlayerCombatState?.TurnNumber}"));
            _events.Add("turn_start", state.CurrentSide.ToString(),
                $"round={state.RoundNumber} turn={turn0} players=[{players}]", 0,
                state.RoundNumber, turn0, _tree.CurrentId,
                actorId: PlayerScope.LocalNetId, stream: "turn", frontier: FrontierText());

            if (state.CurrentSide != CombatSide.Player)
            {
                return;
            }

            Capture($"第 {turn0} 回合", "turn_start");
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnTurnStarted failed: {e.Message}");
        }
    }

    public void OnCombatEnded()
    {
        (int round, int turn, int nodeId) = Context();
        _events.Add("combat_end", "combat", $"nodes={_tree.Count}", 0, round, turn, nodeId);

        RewindCoordinator.Instance.OnCombatEnded();

        _combatActive = false;
        _suppressCapture = false;
        _actionDepthByOwner.Clear();
        _pendingLabels.Clear();
        ClearPendingCapture();
        _lastPlayerAction = null;
        _lastPlayerActionLabel = null;
        _lastPlayerActionKind = null;
        _store.SaveTree(_tree);
        NotifySessionEnded();
    }

    public void OnRunCleanUp()
    {
        try
        {
            RewindCoordinator.Instance.OnRunCleanUp();

            _combatActive = false;
            _actionDepthByOwner.Clear();
            _pendingLabels.Clear();
            ClearPendingCapture();
            _lastPlayerAction = null;
            _lastPlayerActionLabel = null;
            _lastPlayerActionKind = null;
            _tree.Clear();
            Unsubscribe();
            _events.Clear();
            _store.DeleteAll();
            NotifySessionEnded();
            ModLog.Info("run cleaned up, rewind data removed");
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnRunCleanUp failed: {e.Message}");
        }
    }

    // ------------------------------------------------- 全事件订阅（官方 replay 同款入口）

    /// <summary>
    /// 挂到 RunManager 的同步器上。四个对象都是每局 InitializeShared 新建的，
    /// 所以按引用比对做幂等订阅：RunStarted 和 CombatBegan 都会调，重复调用不会重复挂。
    ///
    /// 入口选择的依据（反编译 sts2.dll 确认）：
    ///   · RequestEnqueue 与 RequestEnqueueHookAction 在单人局最终都落到
    ///     ActionQueueSet.EnqueueWithoutSynchronizing，它在分配 id 后触发 ActionEnqueued；
    ///   · 官方 CombatReplayWriter 也只订阅 ActionEnqueued 就记录了全部动作（含 hook）。
    ///   · 消耗 / 变牌 / 弃牌 / 保留手牌这类"玩家选择"不产生新的 GameAction，
    ///     结果落在 PlayerChoiceSynchronizer.PlayerChoiceReceived，必须单独订阅。
    /// </summary>
    private void EnsureSubscribed()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager == null)
            {
                return;
            }

            ActionQueueSet queueSet = manager.ActionQueueSet;
            PlayerChoiceSynchronizer choices = manager.PlayerChoiceSynchronizer;
            ChecksumTracker checksums = manager.ChecksumTracker;

            if (_subscribed
                && ReferenceEquals(_subQueueSet, queueSet)
                && ReferenceEquals(_subChoiceSynchronizer, choices)
                && ReferenceEquals(_subChecksumTracker, checksums))
            {
                return;
            }

            Unsubscribe();

            _subQueueSet = queueSet;
            _subChoiceSynchronizer = choices;
            _subChecksumTracker = checksums;

            if (queueSet != null)
            {
                queueSet.ActionEnqueued += OnActionEnqueued;
                queueSet.ActionResumed += OnActionResumed;
            }

            if (choices != null)
            {
                choices.PlayerChoiceReceived += OnPlayerChoiceReceived;
            }

            if (checksums != null)
            {
                checksums.ChecksumGenerated += OnChecksumGenerated;
            }

            _subscribed = true;
            ModLog.Info("event log subscribed: ActionQueueSet / PlayerChoiceSynchronizer / ChecksumTracker");
        }
        catch (Exception e)
        {
            ModLog.Warn($"EnsureSubscribed failed: {e.Message}");
        }
    }

    private void Unsubscribe()
    {
        try
        {
            if (_subQueueSet != null)
            {
                _subQueueSet.ActionEnqueued -= OnActionEnqueued;
                _subQueueSet.ActionResumed -= OnActionResumed;
            }

            if (_subChoiceSynchronizer != null)
            {
                _subChoiceSynchronizer.PlayerChoiceReceived -= OnPlayerChoiceReceived;
            }

            if (_subChecksumTracker != null)
            {
                _subChecksumTracker.ChecksumGenerated -= OnChecksumGenerated;
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"Unsubscribe failed: {e.Message}");
        }
        finally
        {
            _subQueueSet = null;
            _subChoiceSynchronizer = null;
            _subChecksumTracker = null;
            _subscribed = false;
        }
    }

    /// <summary>所有 GameAction 的唯一入队点（含 hook），不再是白名单。</summary>
    private void OnActionEnqueued(GameAction action)
    {
        try
        {
            if (action == null)
            {
                return;
            }

            // 新动作入队不再清掉待抓点：它代表的状态正是“这个新动作之前”的边界，
            // 快速连点时恰恰是要把上一个动作的结算状态留下来。是否仍然成立由抓点判据决定。
            (int round, int turn, int nodeId) = Context();
            string type = action.GetType().Name;
            _events.Add("action_enqueued", type, Describe(action),
                action.Id ?? 0, round, turn, nodeId,
                actorId: action.OwnerId,
                stream: action is GenericHookGameAction ? "hook" : "action",
                logicalId: action.Id ?? 0,
                frontier: FrontierText());

            if (!_combatActive)
            {
                return;
            }

            _actionDepthByOwner.TryGetValue(action.OwnerId, out int depth);
            _actionDepthByOwner[action.OwnerId] = depth + 1;
            _pendingLabels[action] = Describe(action);
            action.AfterFinished += OnActionFinished;
            action.BeforeCancelled += OnActionCancelled;
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnActionEnqueued failed: {e.Message}");
        }
    }

    private void OnActionFinished(GameAction action)
    {
        SettleAction(action, "action_finished");
    }

    private void OnActionCancelled(GameAction action)
    {
        SettleAction(action, "action_cancelled");
    }

    /// <summary>
    /// 完成和取消都可能触发（官方自己也是同时挂 AfterFinished + BeforeCancelled 做清理）。
    /// 用 _pendingLabels.Remove 做幂等，避免同一次动作被两条回调各减一次 depth。
    /// </summary>
    private void SettleAction(GameAction action, string kind)
    {
        try
        {
            if (action == null)
            {
                return;
            }

            action.AfterFinished -= OnActionFinished;
            action.BeforeCancelled -= OnActionCancelled;

            if (!_pendingLabels.Remove(action, out string label))
            {
                return;
            }

            (int round, int turn, int nodeId) = Context();
            _events.Add(kind, action.GetType().Name, label ?? action.GetType().Name,
                action.Id ?? 0, round, turn, nodeId,
                actorId: action.OwnerId,
                stream: action is GenericHookGameAction ? "hook" : "action",
                logicalId: action.Id ?? 0,
                frontier: FrontierText());

            if (_actionDepthByOwner.TryGetValue(action.OwnerId, out int depth))
            {
                depth = Math.Max(0, depth - 1);
                if (depth == 0)
                {
                    _actionDepthByOwner.Remove(action.OwnerId);
                }
                else
                {
                    _actionDepthByOwner[action.OwnerId] = depth;
                }
            }

            if (!_combatActive || _suppressCapture)
            {
                return;
            }

            // 记住“最近一个玩家根动作”：它的 hook 还没跑完时，等最后一个 hook 结算，
            // 节点标签仍然写“打出 X”，而不是 hook 的类型名。
            if (IsPlayerRootAction(action))
            {
                _lastPlayerAction = action;
                _lastPlayerActionLabel = label ?? action.GetType().Name;
                _lastPlayerActionKind = KindOf(action);
            }

            // 怪物动作 / ReadyToBeginEnemyTurn 之类既不是玩家操作、也不是玩家操作的收尾 hook：
            // 它们不构成决策边界，直接跳过（也避免敌方回合每动一下都写一条 capture_skipped）。
            if (!ReferenceEquals(action, _lastPlayerAction) && !(action is GenericHookGameAction))
            {
                return;
            }

            // 还有本动作派生出的 hook 没结算：现在抓会固化成“效果只执行了一半”的状态。
            if (HasTrackedHooks)
            {
                return;
            }

            // 只认“刚跑完的这个动作”作为边界；别的动作在跑说明是嵌套内部态。
            GameAction running = RunManager.Instance?.ActionExecutor?.CurrentlyRunningAction;
            if (action.State != GameActionState.Finished
                || (running != null && !ReferenceEquals(running, action)))
            {
                return;
            }

            RewindNode node = TryCaptureAtActionBoundary(action);
            if (node != null)
            {
                _lastPlayerAction = null;
                _lastPlayerActionLabel = null;
                _lastPlayerActionKind = null;
                ClearPendingCapture();
                return;
            }

            // 边界判据没过（例如结束回合过渡/输入已禁用）：留待延迟抓点在下一个静止窗口兜底。
            if (kind == "action_finished"
                && !(PlayerScope.IsMultiplayer && PlayerScope.NetType == NetGameType.Client))
            {
                _pendingCaptureLabel = _lastPlayerActionLabel ?? label ?? "动作";
                _pendingCaptureKind = _lastPlayerActionKind ?? KindOf(action);
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"SettleAction failed: {e.Message}");
        }
    }

    /// <summary>
    /// 动作的逻辑边界抓点：这个动作的 `Execute` 已经完成，下一个动作还没开始。
    /// 队列里可能还排着玩家快速连点时入队的动作 —— 它们属于“未来”，不参与当前快照。
    /// </summary>
    private RewindNode TryCaptureAtActionBoundary(GameAction finishing)
    {
        if (!_combatActive || _suppressCapture || finishing == null)
        {
            return null;
        }

        bool isLastPlayerAction = ReferenceEquals(finishing, _lastPlayerAction);
        if (!isLastPlayerAction && _lastPlayerAction == null)
        {
            // 纯内部动作（回合开始 hook 等），没有可归属的玩家操作，不建点。
            return null;
        }

        string label = _lastPlayerActionLabel ?? finishing.GetType().Name;
        string kind = _lastPlayerActionKind ?? KindOf(finishing);

        // 真联机客户端不建点：抓一份边界快照放进缓存，等房主锚点按 checksum 命中。
        if (PlayerScope.IsMultiplayer && PlayerScope.NetType == NetGameType.Client)
        {
            if (TryCaptureMirrorBoundary(finishing, out uint checksum, out Frontier frontier,
                    out ObjectGraphSnapshot snapshot))
            {
                RewindCoordinator.Instance.StashMirrorBoundary(checksum, frontier, snapshot);
            }

            return null;
        }

        return Capture(label, kind, force: false, boundary: finishing);
    }

    /// <summary>客户端在动作边界抓的镜像快照（不建节点，只进缓存）。</summary>
    private bool TryCaptureMirrorBoundary(GameAction finishing, out uint checksum, out Frontier frontier,
        out ObjectGraphSnapshot snapshot)
    {
        checksum = 0;
        frontier = null;
        snapshot = null;

        if (!_combatActive || _suppressCapture)
        {
            return false;
        }

        if (!QuiescenceProbe.TryCaptureBoundary(finishing, out frontier, out string reason))
        {
            _lastQuiescenceReason = reason;
            return false;
        }

        if (!CombatChecksum.ComputePure(out checksum))
        {
            return false;
        }

        try
        {
            snapshot = ObjectGraphSnapshot.Capture(Roots());
        }
        catch (Exception e)
        {
            ModLog.Warn($"mirror boundary snapshot failed: {e.GetType().Name} {e.Message}");
            return false;
        }

        return snapshot != null;
    }

    /// <summary>玩家驱动的根动作：出牌 / 药水 / 结束回合（排除 hook、ReadyToBeginEnemyTurn、怪物动作）。</summary>
    private static bool IsPlayerRootAction(GameAction action)
    {
        if (action == null || !ActionQueueSet.IsGameActionPlayerDriven(action))
        {
            return false;
        }

        ulong? owner = action.OwnerId;
        if (!owner.HasValue)
        {
            return false;
        }

        foreach (Player player in PlayerScope.All)
        {
            if (player != null && player.NetId == owner.Value)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>还有本动作派生出的 hook 没结算（`_pendingLabels` 里剩下的 hook）。</summary>
    private bool HasTrackedHooks
    {
        get
        {
            foreach (GameAction tracked in _pendingLabels.Keys)
            {
                if (tracked is GenericHookGameAction)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>动作因玩家选择暂停后被恢复（官方 replay 也记录 resume）。</summary>
    private void OnActionResumed(uint id)
    {
        try
        {
            if (!_combatActive)
            {
                return;
            }

            (int round, int turn, int nodeId) = Context();
            _events.Add("action_resumed", "resume", $"action id {id}",
                id, round, turn, nodeId,
                actorId: PlayerScope.LocalNetId, stream: "action", logicalId: id,
                frontier: FrontierText());
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnActionResumed failed: {e.Message}");
        }
    }

    /// <summary>
    /// 玩家选择结果（消耗 / 变牌 / 弃牌 / 保留手牌 / 选牌 / 选目标 ...）的最终落点。
    /// 这些操作不会各自 new 一个 GameAction，只会把结果同步到这里；
    /// 不订阅这一条，"战斗内所有事件"就会缺一整类。
    /// </summary>
    private void OnPlayerChoiceReceived(Player player, uint choiceId,
        MegaCrit.Sts2.Core.Entities.Multiplayer.NetPlayerChoiceResult result)
    {
        try
        {
            if (!_combatActive)
            {
                return;
            }

            (int round, int turn, int nodeId) = Context();
            string type = result.type.ToString();
            _events.Add("player_choice", type, $"choice_id={choiceId}",
                choiceId, round, turn, nodeId,
                actorId: player?.NetId ?? 0, stream: "choice", logicalId: choiceId,
                frontier: FrontierText());
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnPlayerChoiceReceived failed: {e.Message}");
        }
    }

    /// <summary>校验和生成。ChecksumTracker.NextId 是快照里恢复的确定性计数器之一。</summary>
    private void OnChecksumGenerated(
        MegaCrit.Sts2.Core.Entities.Multiplayer.NetChecksumData data,
        string context,
        MegaCrit.Sts2.Core.Entities.Multiplayer.NetFullCombatState fullState)
    {
        try
        {
            if (!_combatActive)
            {
                return;
            }

            (int round, int turn, int nodeId) = Context();
            _events.Add("checksum", "generated", context ?? string.Empty,
                0, round, turn, nodeId,
                actorId: PlayerScope.LocalNetId, stream: "checksum", logicalId: data.id,
                frontier: FrontierText());
        }
        catch (Exception e)
        {
            ModLog.Warn($"OnChecksumGenerated failed: {e.Message}");
        }
    }

    /// <summary>事件流水账用的现场坐标：回合 / 玩家回合号 / 当前节点。</summary>
    private (int Round, int Turn, int NodeId) Context()
    {
        try
        {
            CombatState combat = GetCombatState();
            int round = combat?.RoundNumber ?? 0;
            Player me = PlayerScope.Local;
            int turn = me?.PlayerCombatState?.TurnNumber ?? 0;
            return (round, turn, _tree.CurrentId);
        }
        catch
        {
            return (0, 0, _tree.CurrentId);
        }
    }

    /// <summary>流水账用的紧凑切面文本；读不到就留空，不影响主流程。</summary>
    private static string FrontierText()
    {
        try
        {
            return Frontier.Capture(PlayerScope.CombatState)?.Describe();
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- 抓快照

    /// <summary>
    /// 单人局的“本地稳定决策点”——只有这里才允许抓快照、才允许成为回溯树节点。
    ///
    /// 判据全部取自游戏自己的语义（反编译 CombatManager / PlayerCombatState 确认）：
    ///   1) CombatState.CurrentSide == Player：敌方回合里的 hook / 结算动作不算；
    ///   2) !CombatManager.PlayerActionsDisabled：按下结束回合按钮时立刻置 true；
    ///   3) !CombatManager.IsPlayerReadyToEndTurn(本地玩家)：PaelsEye 额外回合里本地玩家
    ///      可能被自动置为“已结束”，此时不能再抓一个“按钮是撤销态”的点；
    ///   4) 本地玩家 PlayerCombatState.Phase == PlayerTurnPhase.Play：官方定义的
    ///      “允许手动打牌和使用药水”阶段。
    ///
    /// 多人局不走这里，走 <see cref="QuiescenceProbe"/>：要求所有活跃玩家同时可操作、
    /// 所有队列为空。两套判据分开，避免把单人局的门禁改严导致节点消失。
    /// </summary>
    private static bool IsStableDecisionPoint(out string reason)
    {
        reason = null;

        CombatState combat = GetCombatState();
        if (combat == null)
        {
            reason = "没有可读取的战斗状态";
            return false;
        }

        if (combat.CurrentSide != CombatSide.Player)
        {
            reason = $"当前是 {combat.CurrentSide} 侧（非玩家回合）";
            return false;
        }

        Player me = PlayerScope.Local;
        if (me == null)
        {
            reason = "找不到本地玩家";
            return false;
        }

        if (me.PlayerCombatState == null)
        {
            reason = "本地玩家没有 PlayerCombatState";
            return false;
        }

        CombatManager manager = CombatManager.Instance;
        if (manager != null && manager.PlayerActionsDisabled)
        {
            reason = "玩家动作被禁用（结束回合过渡中）";
            return false;
        }

        if (manager != null && manager.IsPlayerReadyToEndTurn(me))
        {
            reason = "本地玩家已结束回合";
            return false;
        }

        PlayerTurnPhase phase = me.PlayerCombatState.Phase;
        if (phase != PlayerTurnPhase.Play)
        {
            reason = $"玩家回合阶段为 {phase}（非 Play，回合过渡中）";
            return false;
        }

        return true;
    }

    private RewindNode Capture(string label, string kind, bool force = false, GameAction boundary = null)
    {
        if (!_combatActive || _suppressCapture)
        {
            return null;
        }

        // 真联机里节点 id 由房主分配：客户端不自己建点，只镜像房主广播的锚点。
        // 客户端仍然每帧跑 QuiescenceProbe（状态展示要用），但不产生任何编号。
        if (PlayerScope.IsMultiplayer && PlayerScope.NetType == NetGameType.Client)
        {
            if (!force)
            {
                QuiescenceProbe.TryCapture(out _, out string clientReason);
                _lastQuiescenceReason = clientReason;
            }

            return null;
        }

        bool multiplayerLike = PlayerScope.IsMultiplayerLike;
        Frontier frontier = null;

        // force 只用于战斗开始：那一刻可能仍处在 Start 阶段，但仍需要一个根节点。
        // 其余所有抓点（动作结算完、回合开始、手动 rew_capture）都必须落在稳定点。
        // 单人局用本地判据；多人局用“全体静止点”判据（所有活跃玩家可操作 + 队列全空）。
        if (multiplayerLike)
        {
            if (!force)
            {
                // boundary != null：这是一个动作刚刚执行完的“逻辑边界”，
                // 队列里可能还排着下一个动作 —— 那是未来，不属于当前状态。
                bool ok;
                string quiescenceReason;
                if (boundary != null)
                {
                    ok = QuiescenceProbe.TryCaptureBoundary(boundary, out frontier, out quiescenceReason);
                }
                else
                {
                    ok = QuiescenceProbe.TryCapture(out frontier, out quiescenceReason);
                }

                if (!ok)
                {
                    _lastQuiescenceReason = quiescenceReason;
                    LastCaptureSkipReason = quiescenceReason ?? "非全体静止点";
                    (int roundS, int turnS, int nodeS) = Context();
                    _events.Add("capture_skipped", kind, $"'{label}': {LastCaptureSkipReason}",
                        0, roundS, turnS, nodeS,
                        actorId: PlayerScope.LocalNetId, stream: "system",
                        frontier: FrontierText());
                    return null;
                }

                _lastQuiescenceReason = null;
            }
            else
            {
                // 战斗开始的根节点不要求所有人都已经进入 Play；它只作为“起点记录”，
                // 不参与跳转（Playable=false）。
                frontier = Frontier.Capture(GetCombatState());
            }
        }
        else if (!force && !IsStableDecisionPoint(out string unstableReason))
        {
            LastCaptureSkipReason = unstableReason ?? "非稳定决策点";
            (int roundS, int turnS, int nodeS) = Context();
            _events.Add("capture_skipped", kind, $"'{label}': {LastCaptureSkipReason}",
                0, roundS, turnS, nodeS,
                actorId: PlayerScope.LocalNetId, stream: "system",
                frontier: FrontierText());
            return null;
        }

        LastCaptureSkipReason = null;
        frontier ??= Frontier.Capture(GetCombatState());

        // 纯哈希：不推进 ChecksumTracker.NextId、不发消息；多人抓点/跳转都只走这一条。
        if (!CombatChecksum.ComputePure(out uint checksum))
        {
            return null;
        }

        // 先抓快照再做合并判断：合并的判据不能只看校验和。
        // 校验和（游戏自己的 NetFullCombatState）不包含回合流程状态 —— 按下结束回合的那一刻
        // 战斗数据没变、只有"谁已就绪"变了，只看校验和会把这两个状态当成同一个节点合并掉，
        // 跳回去就会拿到"还没按结束回合"的快照，语义上是错的。
        ObjectGraphSnapshot snapshot;
        try
        {
            snapshot = ObjectGraphSnapshot.Capture(Roots());
        }
        catch (Exception e)
        {
            ModLog.Warn($"capture failed: {e.GetType().Name} {e.Message}");
            return null;
        }

        string deepHash = snapshot.DeepHash;
        string sharedDeepHash = snapshot.SharedDeepHash;
        RewindNode current = _tree.Current;

        bool sameState = current != null && current.Checksum != 0 && current.Checksum == checksum;
        if (sameState)
        {
            // 多人局必须同时比切面：同一动作的前后两个阶段 checksum 可能相同，
            // 只有 ready 集合 / choice 计数器变了，只看 checksum 会误合并。
            sameState = multiplayerLike
                ? current.Frontier != null
                  && current.Frontier.SameAs(frontier)
                  && string.Equals(current.SharedDeepHash, sharedDeepHash, StringComparison.Ordinal)
                : string.Equals(current.DeepHash, deepHash, StringComparison.Ordinal);
        }

        if (!force && sameState)
        {
            // 状态没有变化（最常见：战斗刚开始时回合开始的快照与根节点完全一致）。
            // 语义上就是"合并到当前节点"：不新增节点，但把当前节点返回给调用方。
            (int roundM, int turnM, _) = Context();
            _events.Add("capture_merged", kind, $"identical to node #{current.Id}",
                0, roundM, turnM, current.Id,
                actorId: PlayerScope.LocalNetId, stream: "system",
                frontier: frontier?.Describe());
            ModLog.Info($"state identical to node #{current.Id}, merged");
            BroadcastCurrentIfHost(current.Id);
            return current;
        }

        // 全局找同态节点（不限于兄弟）：同一状态只保留一个“规范节点”，
        // 规范节点取同态节点里**最深**的一个；出现更深的就立刻换主（旧主退化成传送门）。
        RewindNode twin = null;
        bool switchCanonical = false;
        int demotedId = 0;
        bool twinExact = true;

        if (!force)
        {
            twin = FindCanonical(checksum, snapshot, frontier, multiplayerLike, out twinExact);

            if (twin != null && twin.Id == current?.Id)
            {
                // 深拷贝指纹不等、但合并哈希命中当前节点：仍按“状态没变”处理。
                (int roundM, int turnM, _) = Context();
                _events.Add("capture_merged", kind, $"identical to node #{current.Id} (merge hash)",
                    0, roundM, turnM, current.Id,
                    actorId: PlayerScope.LocalNetId, stream: "system",
                    frontier: frontier?.Describe());
                BroadcastCurrentIfHost(current.Id);
                return current;
            }

            if (twin != null)
            {
                int currentId = current?.Id ?? 0;

                // 祖先/后代关系：这一步只是回到已有路径上，不建节点也不建传送门。
                if (_tree.IsAncestor(twin.Id, currentId) || _tree.IsAncestor(currentId, twin.Id))
                {
                    _tree.CurrentId = twin.Id;
                    _store.SaveTree(_tree);
                    (int roundR, int turnR, _) = Context();
                    _events.Add("capture_related", kind,
                        $"state equal to related node #{twin.Id}, no portal",
                        0, roundR, turnR, twin.Id,
                        actorId: PlayerScope.LocalNetId, stream: "system",
                        frontier: frontier?.Describe());
                    ModLog.Info($"state equal to related node #{twin.Id}, switched current only");
                    BroadcastCurrentIfHost(twin.Id);
                    NotifyChanged();
                    return twin;
                }

                int depthNew = _tree.Depth(currentId) + 1;
                int depthTwin = _tree.Depth(twin.Id);

                if (depthNew > depthTwin)
                {
                    // 更深：连节点 + 换主，走下面的正常建点分支。
                    switchCanonical = true;
                    demotedId = twin.Id;
                }
                else
                {
                    // 不更深（同深度或更浅）：建传送门，current 仍是规范节点。
                    RewindNode portal = _tree.AddPortal(currentId, twin.Id, label, kind, out string portalError);
                    if (portal == null)
                    {
                        ModLog.Warn($"add portal failed: {portalError}");
                        return twin;
                    }

                    LastPortalId = portal.Id;
                    portal.MergeExact = twinExact;
                    portal.Shared = !PlayerScope.IsMultiplayer;
                    portal.Playable = twin.Playable;
                    portal.State = PlayerScope.IsMultiplayer ? "pending" : multiplayerLike ? "local" : "sp";

                    _store.SaveTree(_tree);
                    _store.SaveNode(portal);
                    (int roundP, int turnP, _) = Context();
                    _events.Add("capture_portal", kind,
                        $"portal #{portal.Id} '{label}' -> canonical #{twin.Id} "
                        + $"(depth {depthNew} vs {depthTwin}, checksum {checksum:x8})",
                        0, roundP, turnP, portal.Id,
                        actorId: PlayerScope.LocalNetId, stream: "system",
                        frontier: frontier?.Describe());
                    ModLog.Info($"portal #{portal.Id} '{label}' -> canonical #{twin.Id} "
                                + $"(depth {depthNew} vs {depthTwin}, checksum {checksum:x8})");

                    if (PlayerScope.IsMultiplayer)
                    {
                        RewindCoordinator.Instance.OnHostNodeCreated(portal);
                    }

                    BroadcastCurrentIfHost(twin.Id);
                    NotifyChanged();
                    return twin;
                }
            }
        }

        CombatState combat = GetCombatState();
        int round = combat?.RoundNumber ?? 0;
        int turn = PlayerScope.Local?.PlayerCombatState?.TurnNumber ?? 0;

        // 普通建点会把“最近一次传送门”清掉，避免高亮一直挂在旧节点上；
        // 换主分支随后会重新写上（刚被降级的旧主）。
        LastPortalId = 0;

        RewindNode node = _tree.Add(current?.Id ?? 0, label, kind, snapshot, checksum, round, turn, out string error);
        if (node == null)
        {
            ModLog.Warn($"add node failed: {error}");
            return null;
        }

        node.DeepHash = deepHash;
        node.SharedDeepHash = sharedDeepHash;
        node.Frontier = frontier;
        // 单人/假多人：本进程内即“全端一致”。真联机：等所有客户端 ack 之后
        // 由 RewindCoordinator 改成 shared，在此之前是 pending（不可跳）。
        node.Shared = !PlayerScope.IsMultiplayer;
        // 战斗开始的根节点在多人局里只作记录：Phase 可能还是 Start，没有下一次 TurnStarted
        // 把它推到 Play，跳过去会得到一个“牌打不出去”的死局。
        node.Playable = !(multiplayerLike && force);
        node.State = PlayerScope.IsMultiplayer ? "pending" : multiplayerLike ? "local" : "sp";
        node.Participants = PlayerScope.All
            .Where(player => player != null)
            .Select(player => player.NetId)
            .OrderBy(id => id)
            .ToArray();
        node.SharedObjectCount = snapshot.SharedObjectCount;
        node.SharedFieldCount = snapshot.SharedFieldCount;

        if (CombatChecksum.TryBuildNormalized(out var normalized, out _))
        {
            node.Digest = CombatChecksum.BuildDigest(normalized);
        }

        // 更深 → 立刻换主：新节点接管规范身份，旧主的子树整体搬到新节点下，
        // 旧主退化成叶子传送门。必须在落盘之前完成，否则 tree.json 会留下旧形状。
        if (switchCanonical && demotedId != 0)
        {
            node.MergeExact = twinExact;
            if (_tree.DemoteCanonical(demotedId, node.Id, out int movedChildren, out string demoteError))
            {
                (int roundS, int turnS, _) = Context();
                _events.Add("capture_canonical_switch", kind,
                    $"node #{node.Id} took canon from #{demotedId}, moved {movedChildren} children",
                    0, roundS, turnS, node.Id,
                    actorId: PlayerScope.LocalNetId, stream: "system",
                    frontier: frontier?.Describe());
                LastPortalId = demotedId;
                ModLog.Info($"canonical switch: #{node.Id} replaces #{demotedId} "
                            + $"({movedChildren} children moved, depth {_tree.Depth(node.Id)})");
            }
            else
            {
                ModLog.Warn($"canonical switch #{demotedId} -> #{node.Id} failed: {demoteError}");
            }
        }

        _store.SaveTree(_tree);
        _store.SaveNode(node);
        (int roundC, int turnC, _) = Context();
        _events.Add("capture", kind,
            $"node #{node.Id} '{label}' checksum={node.Checksum:x8} objects={node.ObjectCount} "
            + $"shared={node.Shared} playable={node.Playable} frontier={frontier?.Describe()}",
            0, roundC, turnC, node.Id,
            actorId: PlayerScope.LocalNetId, stream: "system",
            frontier: frontier?.Describe());
        ModLog.Info($"node #{node.Id} '{label}' ({node.ObjectCount} objects, checksum {node.Checksum:x8}, "
                    + $"shared={node.Shared}, playable={node.Playable})");

        // 房主：立刻广播锚点，让客户端在同一个逻辑点抓自己的 memento。
        // 换主时把旧主 id 一起带过去，客户端在绑定新节点后按同一顺序重放换主。
        if (PlayerScope.IsMultiplayer)
        {
            RewindCoordinator.Instance.OnHostNodeCreated(node, switchCanonical ? demotedId : 0);
        }

        NotifyChanged();
        return node;
    }

    /// <summary>重放同一动作会合并到旧节点（不新建），房主要把“当前节点”同步给客户端。</summary>
    private static void BroadcastCurrentIfHost(int nodeId)
    {
        if (PlayerScope.IsMultiplayer && PlayerScope.NetType == NetGameType.Host)
        {
            RewindCoordinator.Instance.BroadcastCurrent(nodeId);
        }
    }

    private static SnapshotRoot[] Roots()
    {
        RunManager manager = RunManager.Instance;
        CombatManager combat = CombatManager.Instance;

        // 共享根必须排在本地根前面：同一个对象先从共享路径访问到，就会标成 Shared。
        // 官方 CombatReplay 的"初始状态"四件套：动作 id、hook id、玩家选择 id、校验和 id。
        // 这些编号决定后续每个动作 / hook / 选择的分流，不恢复就无法保证
        // "跳转到节点" 与 "从战斗开始按操作树重放到该节点" 完全等价。
        // 每个类型在 MementoRules 里都有字段白名单，只会读这几个标量，
        // 不会顺着 _netService / _messageBuffer / 事件委托往下遍历。
        var roots = new List<SnapshotRoot>
        {
            new SnapshotRoot(manager?.DebugOnlyGetState(), SnapshotScope.Shared, "run"),
            new SnapshotRoot(combat?.History, SnapshotScope.Shared, "history"),
            new SnapshotRoot(combat?.StateTracker, SnapshotScope.Shared, "state_tracker"),
            new SnapshotRoot(GetTurnState(), SnapshotScope.Shared, "turn_state"),
            new SnapshotRoot(GetCombatState(), SnapshotScope.Shared, "combat_state"),
            new SnapshotRoot(manager?.ActionQueueSet, SnapshotScope.Shared, "action_queue"),
            new SnapshotRoot(manager?.ActionQueueSynchronizer, SnapshotScope.Shared, "action_synchronizer"),
            new SnapshotRoot(manager?.PlayerChoiceSynchronizer, SnapshotScope.Shared, "choice_synchronizer")
        };

        if (!PlayerScope.IsMultiplayer)
        {
            // 单人局保留 NextId，维持“跳转 == 重放”的 checksum 编号一致。
            // 多人局不回滚它：回滚会让 checksum id 与对端错位，触发 StateDivergence。
            roots.Add(new SnapshotRoot(manager?.ChecksumTracker, SnapshotScope.Shared, "checksum_tracker"));
        }

        // CombatManager 最后：它只白名单了 _playerActionsDisabled（本地视图），
        // 其余对象已经从共享根访问过，不会被误标成本地。
        roots.Add(new SnapshotRoot(combat, SnapshotScope.Local, "combat_manager"));
        return roots.ToArray();
    }

    /// <summary>
    /// 回合流程状态（CombatTurnState）：结束回合的就绪集合、额外回合名单（PaelsEye）、侧切换标志都在这。
    /// 它不是 CombatState 的一部分，从 RunState 走过去够不到，只能单独当根。
    /// 字段写回由 MementoRules 的白名单控制（锁 / TCS / CTS 一律不碰）。
    /// </summary>
    private static FieldInfo _turnStateField;

    private static object GetTurnState()
    {
        try
        {
            CombatManager combat = CombatManager.Instance;
            if (combat == null)
            {
                return null;
            }

            _turnStateField ??= typeof(CombatManager).GetField(
                "_turnState", BindingFlags.Instance | BindingFlags.NonPublic);

            return _turnStateField?.GetValue(combat);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在同一父节点下找 checksum 与深拷贝指纹都完全相同的兄弟节点。
    /// 只做兄弟级合并：跨分支的相同状态仍然保留，因为它们的来路不同。
    ///
    /// 合并用两套指纹：完整指纹相同直接合并；否则用 `MergeHash`（忽略已分配但还没执行的
    /// 动作编号）再比一次 —— 快速连点时“A 结算完”的快照里已经带上了排队中 B 的编号，
    /// 模型状态其实是同一个，不该多出一个重复节点。
    /// </summary>
    /// <summary>
    /// 全局找同态规范节点：checksum 先筛，再比深拷贝指纹（多人加切面）。
    /// 跳过传送门（AliasOf != 0）、不可跳、无内存快照、失效的节点。
    ///
    /// 旧版本可能留下多个同态规范节点（兄弟级合并的漏网），这里取**最深**的一个，
    /// 同深度取 id 最小的，保证结果确定。
    /// </summary>
    private RewindNode FindCanonical(uint checksum, ObjectGraphSnapshot snapshot,
        Frontier frontier, bool multiplayerLike, out bool exact)
    {
        exact = true;

        if (checksum == 0 || snapshot == null)
        {
            return null;
        }

        RewindNode best = null;
        int bestDepth = int.MinValue;
        bool bestExact = true;

        foreach (RewindNode node in _tree.Nodes.Values)
        {
            if (node.AliasOf != 0 || node.Checksum != checksum || !node.Playable)
            {
                continue;
            }

            if (node.State == "stale" || node.Snapshot == null)
            {
                continue;
            }

            // 先看逐字节相同；否则退到“可玩状态相同”（checksum + 切面，忽略日志与动作编号）。
            bool isExact = SameNodeState(node, checksum, snapshot, frontier, multiplayerLike);
            bool sameState = isExact
                             || (frontier != null && node.Frontier != null
                                 && node.Frontier.SameStateAs(frontier));

            if (!sameState)
            {
                continue;
            }

            int depth = _tree.Depth(node.Id);
            if (depth > bestDepth || (depth == bestDepth && (best == null || node.Id < best.Id)))
            {
                best = node;
                bestDepth = depth;
                bestExact = isExact;
            }
        }

        if (best != null)
        {
            exact = bestExact;
        }

        return best;
    }

    private static bool SameNodeState(RewindNode node, uint checksum, ObjectGraphSnapshot snapshot,
        Frontier frontier, bool multiplayerLike)
    {
        if (node == null || node.Checksum == 0 || node.Checksum != checksum)
        {
            return false;
        }

        if (!multiplayerLike)
        {
            if (string.Equals(node.DeepHash, snapshot.DeepHash, StringComparison.Ordinal))
            {
                return true;
            }

            return node.Snapshot != null
                   && string.Equals(node.Snapshot.MergeHash, snapshot.MergeHash, StringComparison.Ordinal);
        }

        // 多人口径先用严格的 frontier + 共享哈希；不成立再用“忽略已分配动作编号”的合并哈希兜底。
        // 后者覆盖了 frontier 的全部来源字段（phase / ready / 回合），只是不把排队动作的编号算进去。
        if (node.Frontier != null
            && node.Frontier.SameAs(frontier)
            && string.Equals(node.SharedDeepHash, snapshot.SharedDeepHash, StringComparison.Ordinal))
        {
            return true;
        }

        return node.Snapshot != null
               && string.Equals(node.Snapshot.SharedMergeHash, snapshot.SharedMergeHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// 自测用的抓点入口：在当前状态再抓一次，用于验证"全同节点合并"。
    /// </summary>
    public RewindNode CaptureNowForTest(string label)
    {
        return Capture(label ?? "自测", "test");
    }

    private static string Truncate(string text, int max = 2500)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        return text.Length <= max ? text : text.Substring(0, max) + "\n...(truncated)";
    }

    private static CombatState GetCombatState()
    {
        return PlayerScope.CombatState;
    }

    // ---------------------------------------------------------------- 多人协同（M2）

    /// <summary>冻结时抓的安全快照：跳转失败要原样退回来。</summary>
    internal ObjectGraphSnapshot CaptureSafetySnapshot()
    {
        try
        {
            return ObjectGraphSnapshot.Capture(Roots());
        }
        catch (Exception e)
        {
            ModLog.Warn($"safety snapshot failed: {e.GetType().Name} {e.Message}");
            return null;
        }
    }

    /// <summary>协同跳转期间禁止本地抓点（含延迟抓点）。</summary>
    internal void SetCaptureSuspended(bool suspended)
    {
        _suppressCapture = suspended;
        if (suspended)
        {
            ClearPendingCapture();
        }
    }

    internal void NotifyChangedPublic()
    {
        NotifyChanged();
    }

    internal void LogMpEvent(string kind, string detail, int nodeId = 0, ulong actorId = 0)
    {
        try
        {
            (int round, int turn, int node) = Context();
            _events.Add(kind, "mp", detail, 0, round, turn, nodeId != 0 ? nodeId : node,
                actorId: actorId != 0 ? actorId : PlayerScope.LocalNetId,
                stream: "mp",
                frontier: FrontierText());
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp event log failed: {e.Message}");
        }
    }

    /// <summary>
    /// 客户端镜像锚点：只有当前状态与房主是**同一个逻辑点**（纯哈希 + 切面都相同）才抓快照。
    /// force 用于战斗开始的根节点——它只是起点记录，两端各自记录自己那一刻即可。
    /// </summary>
    internal bool TryCaptureMirrorPoint(uint expectedChecksum, Frontier expectedFrontier, bool force,
        out ObjectGraphSnapshot snapshot, out uint checksum, out Frontier frontier, out string reason)
    {
        snapshot = null;
        checksum = 0;
        frontier = null;
        reason = null;

        if (!_combatActive)
        {
            reason = "不在战斗中";
            return false;
        }

        CombatState combat = GetCombatState();
        if (combat == null)
        {
            reason = "没有可读取的战斗状态";
            return false;
        }

        if (!CombatChecksum.ComputePure(out checksum))
        {
            reason = "无法计算校验和";
            return false;
        }

        frontier = Frontier.Capture(combat);

        if (!force)
        {
            // 必须在“本机也静止”的同一帧抓：动作收尾的那一帧模型已经等于锚点，
            // 但还有一批瞬时字段（卡牌指向目标、动画引用）没清掉；这时候抓出来的
            // 节点和恢复后的立刻状态对不上（实测会命中 CardModel._currentTarget）。
            if (!QuiescenceProbe.TryCapture(out Frontier probed, out string probeReason))
            {
                reason = probeReason;
                return false;
            }

            frontier = probed;

            if (expectedChecksum != 0 && checksum != expectedChecksum)
            {
                reason = $"状态与锚点不同（{checksum:x8} != {expectedChecksum:x8}）";
                return false;
            }

            if (expectedFrontier != null && frontier != null && !expectedFrontier.SameAs(frontier))
            {
                reason = "切面与锚点不同（可能是本端还停在过渡态）";
                return false;
            }
        }

        try
        {
            snapshot = ObjectGraphSnapshot.Capture(Roots());
        }
        catch (Exception e)
        {
            reason = $"快照失败：{e.GetType().Name} {e.Message}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// 客户端建镜像节点。编号、父子关系都由房主锚点决定；
    /// partial 节点没有快照，只用来占住编号空间（不可跳）。
    /// </summary>
    internal RewindNode AddMirroredNode(int id, int parentId, string label, string kind,
        Frontier frontier, uint checksum, ObjectGraphSnapshot snapshot, bool playable, bool shared,
        string state, out string error, int aliasOf = 0)
    {
        int beforeCurrent = _tree.CurrentId;

        RewindNode node = _tree.AddWithId(id, parentId, label, kind, snapshot, checksum,
            frontier?.RoundNumber ?? GetCombatState()?.RoundNumber ?? 0,
            PlayerScope.Local?.PlayerCombatState?.TurnNumber ?? 0,
            out error);

        if (node == null)
        {
            return null;
        }

        node.Frontier = frontier;
        node.DeepHash = snapshot?.DeepHash;
        node.SharedDeepHash = snapshot?.SharedDeepHash;
        node.Shared = shared;
        node.Playable = playable;
        node.State = state ?? "shared";
        node.Participants = PlayerScope.All
            .Where(player => player != null)
            .Select(player => player.NetId)
            .OrderBy(netId => netId)
            .ToArray();
        node.SharedObjectCount = snapshot?.SharedObjectCount ?? 0;
        node.SharedFieldCount = snapshot?.SharedFieldCount ?? 0;

        // 传送门：共享规范节点的 memento，本身不持有快照，也不推进 current。
        if (aliasOf != 0)
        {
            RewindNode canonical = _tree.Get(aliasOf);
            node.AliasOf = canonical != null ? canonical.Id : aliasOf;
            if (canonical != null)
            {
                node.Snapshot = canonical.Snapshot;
                node.DeepHash = canonical.DeepHash;
                node.SharedDeepHash = canonical.SharedDeepHash;
                node.ObjectCount = canonical.ObjectCount;
                node.FieldCount = canonical.FieldCount;
                node.SharedObjectCount = canonical.SharedObjectCount;
                node.SharedFieldCount = canonical.SharedFieldCount;
                node.Digest = canonical.Digest;
                node.Frontier = canonical.Frontier;
                beforeCurrent = canonical.Id;
                _tree.CurrentId = canonical.Id;
            }
        }

        if (CombatChecksum.TryBuildNormalized(out var normalized, out _))
        {
            node.Digest = CombatChecksum.BuildDigest(normalized);
        }

        // 不可跳的节点不改变“当前节点”：它是房主那边的记录，不是本端所在位置。
        if (!playable)
        {
            _tree.CurrentId = beforeCurrent != 0 ? beforeCurrent : id;
        }

        _store.SaveTree(_tree);
        _store.SaveNode(node);

        (int round, int turn, _) = Context();
        _events.Add("mirror", kind ?? "state",
            $"node #{id} '{label}' checksum={checksum:x8} state={node.State} "
            + $"playable={playable} has_snapshot={snapshot != null}",
            0, round, turn, id,
            actorId: PlayerScope.LocalNetId, stream: "system",
            frontier: frontier?.Describe());
        ModLog.Info($"mirrored node #{id} '{label}' ({snapshot?.ObjectCount ?? 0} objects, "
                    + $"checksum {checksum:x8}, state={node.State})");
        NotifyChanged();
        return node;
    }

    /// <summary>锚点 ack 之后更新节点的 shared/partial 标记并落盘。</summary>
    internal void UpdateNodeSyncState(RewindNode node)
    {
        if (node == null)
        {
            return;
        }

        _store.SaveNode(node);
        _store.SaveTree(_tree);
        NotifyChanged();
    }

    /// <summary>
    /// 协同跳转的“本端写回”。安全快照由调用方持有（冻结时抓的那一份）：
    /// 失败时这里直接写回安全快照，成功时由调用方在 Resume/Abort 时决定去留。
    /// </summary>
    internal RestoreOutcome ApplyMpRestore(int id, ObjectGraphSnapshot safety)
    {
        var outcome = new RestoreOutcome();

        RewindNode node = _tree.Get(id);
        if (node == null)
        {
            outcome.Error = $"节点 #{id} 不存在";
            return outcome;
        }

        if (node.Snapshot == null)
        {
            outcome.Error = $"节点 #{id} 没有内存快照";
            return outcome;
        }

        outcome = RestoreNodeSnapshot(node, safety);
        if (outcome.Ok)
        {
            // 测试钩子：模拟“写回成功但复核失败”（例如对端快照被篡改）。
            // 只有显式调用 rew_test_poison_restore 才会置位，用完即清。
            if (RewindCoordinator.Instance.ConsumePoisonedRestore())
            {
                RestoreSafety(safety);
                outcome.Ok = false;
                outcome.Error = "[test] poisoned restore (simulated post-restore mismatch)";
                outcome.DeepEqual = false;
                return outcome;
            }

            // 写回会把 PlayerActionsDisabled/队列暂停这些本地字段恢复成快照里的值；
            // 在 Resume 之前必须重新冻结，否则本端会在别人还没写完时先动起来。
            RewindCoordinator.Instance.KeepFrozen();
        }

        return outcome;
    }

    /// <summary>全员一致：把当前节点推进到目标，并重建本地房间。</summary>
    internal void CommitRestoredNode(int id)
    {
        RewindNode node = _tree.Get(id);
        if (node == null)
        {
            return;
        }

        _tree.CurrentId = id;
        _store.SaveTree(_tree);

        RebuildAfterRestore();

        (int round, int turn, _) = Context();
        _events.Add("jump", "jump",
            $"node #{id} '{node.Label}' checksum={node.Checksum:x8} deep_equal=true (mp)",
            0, round, turn, id);
        ModLog.Info($"mp jump to #{id} '{node.Label}' committed (checksum {node.Checksum:x8})");
        NotifyChanged();
    }

    /// <summary>把当前节点指针挪到指定节点（多人局由房主广播）。</summary>
    internal void SetCurrentNode(int id)
    {
        if (_tree.Get(id) == null)
        {
            return;
        }

        _tree.CurrentId = id;
        _store.SaveTree(_tree);
        NotifyChanged();
    }

    internal void RebuildAfterRestore()
    {
        try
        {
            string rebuildError;
            if (!CombatViewRebuilder.Rebuild(out rebuildError) && !string.IsNullOrEmpty(rebuildError))
            {
                ModLog.Warn($"visual rebuild: {rebuildError}");
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"visual rebuild failed: {e.GetType().Name} {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 跳转

    public bool JumpTo(int id, out string error)
    {
        error = null;

        // 传送门只是别名：点击它等于点击规范节点。解析后再走现有流程。
        int resolvedId = _tree.Resolve(id);
        if (resolvedId != 0 && resolvedId != id)
        {
            _events.Add("jump_resolve_portal", "jump", $"portal #{id} -> canonical #{resolvedId}");
            id = resolvedId;
        }

        (int roundJ, int turnJ, int nodeJ) = Context();
        _events.Add("jump_request", "jump", $"target #{id}",
            0, roundJ, turnJ, nodeJ);

        ClearPendingCapture();

        if (!_combatActive)
        {
            return RejectJump(id, "不在战斗中", out error);
        }

        // 真联机：走协同协议（房主权威）。这里只负责“发起”，
        // Freeze → Idle → Commit → Restored → Resume 由 RewindCoordinator 跨帧推进，
        // 结果用 rew_jump_status / rew_status.mp 查询。
        if (PlayerScope.IsMultiplayer)
        {
            RewindCoordinator.JumpStart start =
                RewindCoordinator.Instance.RequestJump(id, out error, out int requestId);
            if (start == RewindCoordinator.JumpStart.Rejected)
            {
                string reason = error;
                return RejectJump(id, reason, out error);
            }

            _events.Add("jump_pending", "jump",
                start == RewindCoordinator.JumpStart.HostStarted
                    ? $"req={requestId} 房主发起协同跳转 target=#{id}"
                    : $"req={requestId} 已向房主请求 target=#{id}",
                0, roundJ, turnJ, id);
            ModLog.Info($"mp jump requested: req={requestId} target=#{id} ({start})");
            return true;
        }

        if (IsBusy)
        {
            return RejectJump(id, "有动作正在执行，等结算完再跳", out error);
        }

        RewindNode node = _tree.Get(id);
        if (node == null)
        {
            return RejectJump(id, $"节点 #{id} 不存在", out error);
        }

        if (!node.Playable)
        {
            return RejectJump(id, $"节点 #{id} 不是可操作状态（战斗开始根节点只作记录）", out error);
        }

        if (node.Snapshot == null)
        {
            return RejectJump(id, $"节点 #{id} 没有内存快照（落盘数据不参与恢复）", out error);
        }

        if (id == _tree.CurrentId)
        {
            LastRestoreMismatches = 0;
            LastRestoreDeepEqual = true;
            LastRestoreDeepHash = node.DeepHash;
            LastRestoreDeepDetail = $"already at node #{id}, no restore performed";
            LastRestoreReport = $"已在该节点 #{id}，未做恢复";
            _events.Add("jump", "jump", $"already at node #{id}",
                0, roundJ, turnJ, id);
            return true;
        }

        ObjectGraphSnapshot safety = null;
        try
        {
            safety = ObjectGraphSnapshot.Capture(Roots());
        }
        catch
        {
            // 安全快照失败就不做回滚保护，后面校验失败会尽力处理
        }

        _suppressCapture = true;
        try
        {
            RestoreOutcome outcome = RestoreNodeSnapshot(node, safety);

            // 写回之后逐字段回读：这是"捕获到的状态是否真的全回来了"的唯一硬证据。
            // 必须在 NormalizeAfterRestore 之前做 —— 规范化会故意改掉个别瞬时标志。
            LastRestoreMismatches = outcome.Mismatches;
            LastRestoreReport = outcome.Report ?? "no report";
            LastRestoreDeepEqual = outcome.DeepEqual;
            LastRestoreDeepHash = outcome.LiveHash;
            LastRestoreDeepDetail = outcome.DeepDetail ?? "no detail";

            if (!outcome.Ok)
            {
                error = outcome.Error ?? "恢复失败";
                if (!outcome.DeepEqual)
                {
                    ModLog.Warn($"jump #{id} rejected: {error}\n{Truncate(outcome.DeepDetail)}");
                }

                MarkRestoreRejected(id, error);
                return false;
            }

            _tree.CurrentId = id;
            _store.SaveTree(_tree);

            string rebuildError;
            if (!CombatViewRebuilder.Rebuild(out rebuildError) && !string.IsNullOrEmpty(rebuildError))
            {
                ModLog.Warn($"visual rebuild: {rebuildError}");
            }

            (int roundOk, int turnOk, _) = Context();
            _events.Add("jump", "jump",
                $"node #{id} '{node.Label}' checksum={outcome.Checksum:x8} deep_equal=true",
                0, roundOk, turnOk, id);
            ModLog.Info($"jump to #{id} '{node.Label}' ok (checksum {outcome.Checksum:x8})");
            return true;
        }
        catch (Exception e)
        {
            safety?.Restore();
            error = $"{e.GetType().Name}: {e.Message}";
            ModLog.Warn($"jump #{id} failed: {error}");
            MarkRestoreRejected(id, error);
            return false;
        }
        finally
        {
            _suppressCapture = false;
            NotifyChanged();
        }
    }

    /// <summary>
    /// 把节点快照写回活对象，并做三层核对：
    ///   1) Verify：逐字段回读（记录不一致数，供诊断）；
    ///   2) DeepCompare：整图规范序列化后逐字节比对（唯一硬判据）；
    ///   3) 纯哈希：游戏自己的 NetFullCombatState，跨端可比。
    ///
    /// 任一步失败都会把 <paramref name="safety"/> 写回去，然后返回 Ok=false。
    /// 调用方负责 <c>_suppressCapture</c> 的生命周期（SP 是一次调用，MP 要跨帧保持）。
    /// </summary>
    private RestoreOutcome RestoreNodeSnapshot(RewindNode node, ObjectGraphSnapshot safety)
    {
        var outcome = new RestoreOutcome();

        try
        {
            node.Snapshot.Restore();

            VerifyReport report = node.Snapshot.Verify();
            outcome.Mismatches = report.Mismatches;
            outcome.Report = report.ToString();
            if (!report.Clean)
            {
                ModLog.Warn($"node #{node.Id} restored with {report.Mismatches} field mismatch(es): {report}");
            }

            DeepCompareResult deep = node.Snapshot.DeepCompare();
            outcome.DeepEqual = deep.Equal;
            outcome.LiveHash = deep.LiveHash;
            outcome.DeepDetail = deep.ToString();
            if (!deep.Equal)
            {
                outcome.Error = $"深拷贝不一致（期望 {node.DeepHash ?? deep.CapturedHash}，实际 {deep.LiveHash}）";
                RestoreSafety(safety);
                return outcome;
            }

            NormalizeAfterRestore();

            if (!CombatChecksum.ComputePure(out uint actual))
            {
                outcome.Error = "恢复后无法计算校验和";
                RestoreSafety(safety);
                return outcome;
            }

            outcome.Checksum = actual;
            if (node.Checksum != 0 && actual != node.Checksum)
            {
                outcome.Error = $"校验不一致（期望 {node.Checksum:x8}，实际 {actual:x8}）";
                CombatChecksum.TryBuildNormalized(out var liveState, out _);
                string actualDigest = CombatChecksum.BuildDigest(liveState);
                ModLog.Warn($"node #{node.Id} checksum mismatch: {outcome.Error}\n"
                            + $"--- expected ---\n{Truncate(node.Digest)}\n--- actual ---\n{Truncate(actualDigest)}");
                RestoreSafety(safety);
                return outcome;
            }

            outcome.Ok = true;
            return outcome;
        }
        catch (Exception e)
        {
            outcome.Error = $"{e.GetType().Name}: {e.Message}";
            RestoreSafety(safety);
            return outcome;
        }
    }

    private static void RestoreSafety(ObjectGraphSnapshot safety)
    {
        if (safety == null)
        {
            return;
        }

        try
        {
            safety.Restore();
        }
        catch (Exception e)
        {
            ModLog.Warn($"restore safety snapshot failed: {e.GetType().Name} {e.Message}");
        }
    }

    /// <summary>
    /// 拿当前节点快照和活状态逐字段对比。
    /// 只有"刚跳转完、还没继续操作"时才有意义 —— 之后游戏推进，差异是正常的。
    /// </summary>
    public bool VerifyCurrent(out string summary, out int mismatches)
    {
        summary = null;
        mismatches = -1;

        RewindNode node = _tree.Current;
        if (node?.Snapshot == null)
        {
            summary = "当前节点没有内存快照";
            return false;
        }

        try
        {
            VerifyReport report = node.Snapshot.Verify();
            DeepCompareResult deep = node.Snapshot.DeepCompare();
            VerifyReport sharedReport = node.Snapshot.Verify(HashScope.Shared);
            DeepCompareResult sharedDeep = node.Snapshot.DeepCompare(HashScope.Shared);

            mismatches = report.Mismatches + (deep.Equal ? 0 : 1)
                         + sharedReport.Mismatches + (sharedDeep.Equal ? 0 : 1);
            summary = report + "; " + deep + "; shared: " + sharedReport + "; " + sharedDeep;
            return report.Clean && deep.Equal && sharedReport.Clean && sharedDeep.Equal;
        }
        catch (Exception e)
        {
            summary = $"{e.GetType().Name}: {e.Message}";
            return false;
        }
    }

    /// <summary>跳转前置检查失败：统一记一条 jump_rejected 再返回 false。</summary>
    private bool RejectJump(int id, string reason, out string error)
    {
        error = reason;
        (int round, int turn, int nodeId) = Context();
        _events.Add("jump_rejected", "jump", $"target #{id}: {reason}",
            0, round, turn, nodeId);
        return false;
    }

    private void MarkRestoreRejected(int id, string error)
    {
        LastRestoreMismatches = -1;
        LastRestoreDeepEqual = false;
        LastRestoreReport = "跳转被拒绝：" + (error ?? "unknown");
        LastRestoreDeepDetail = LastRestoreReport;
        (int round, int turn, int nodeId) = Context();
        _events.Add("jump_rejected", "restore", $"target #{id}: {error}",
            0, round, turn, nodeId);
    }

    public bool JumpToParent(out string error)    {
        RewindNode current = _tree.Current;
        if (current == null || current.ParentId == 0)
        {
            error = "已经在根节点";
            return false;
        }

        return JumpTo(current.ParentId, out error);
    }

    public bool JumpToForward(out string error)
    {
        RewindNode current = _tree.Current;
        if (current == null || current.Children.Count == 0)
        {
            error = "没有可前进的子节点";
            return false;
        }

        // 传送门不是可展开的状态：前进时跳过，取第一个真正的子节点。
        foreach (int childId in current.Children)
        {
            if (_tree.IsPortal(childId))
            {
                continue;
            }

            return JumpTo(childId, out error);
        }

        error = "子节点都是传送门（点传送门可跳到它指向的节点）";
        return false;
    }

    public bool Prune(int id, out string error)
    {
        error = null;

        (int roundP, int turnP, int nodeP) = Context();
        _events.Add("prune_request", "prune", $"target #{id}",
            0, roundP, turnP, nodeP);

        // 真联机：只有房主能剪，且必须走协同协议（Prepare → 全端 ready → Commit），
        // 保证两端在同一个静止点上删同一棵子树。客户端直接拒绝。
        if (PlayerScope.IsMultiplayer)
        {
            if (PlayerScope.NetType == NetGameType.Client)
            {
                error = "多人局只有房主可以剪枝";
                _events.Add("prune_rejected", "prune", $"target #{id}: {error}",
                    0, roundP, turnP, nodeP);
                return false;
            }

            RewindCoordinator.PruneStart start =
                RewindCoordinator.Instance.RequestPrune(id, out error, out int requestId);
            if (start == RewindCoordinator.PruneStart.Rejected)
            {
                string reason = error;
                _events.Add("prune_rejected", "prune", $"target #{id}: {reason}",
                    0, roundP, turnP, nodeP);
                return false;
            }

            _events.Add("prune_pending", "prune",
                $"req={requestId} 房主发起协同剪枝 target=#{id}",
                0, roundP, turnP, nodeP);
            ModLog.Info($"mp prune requested: req={requestId} target=#{id}");
            return true;
        }

        if (IsBusy)
        {
            error = "有动作正在执行，等结算完再剪枝";
            _events.Add("prune_rejected", "prune", $"target #{id}: {error}",
                0, roundP, turnP, nodeP);
            return false;
        }

        if (!CanPruneTarget(id, out error))
        {
            _events.Add("prune_rejected", "prune", $"target #{id}: {error}",
                0, roundP, turnP, nodeP);
            return false;
        }

        if (!PruneLocal(id, out int removed, out error))
        {
            _events.Add("prune_rejected", "prune", $"target #{id}: {error}",
                0, roundP, turnP, nodeP);
            return false;
        }

        _events.Add("prune", "prune", $"removed #{id} and {removed - 1} descendant(s)",
            0, roundP, turnP, nodeP);
        ModLog.Info($"pruned #{id} and {removed - 1} descendant(s)");
        return true;
    }

    /// <summary>
    /// 剪枝前置条件：目标存在、不是根、当前节点不在该子树里。
    /// 剪掉当前节点会让"显示的当前节点"和"实际战斗状态"对不上，必须先跳到别处。
    /// </summary>
    internal bool CanPruneTarget(int id, out string error)
    {
        error = null;

        RewindNode node = _tree.Get(id);
        if (node == null)
        {
            error = $"节点 #{id} 不存在";
            return false;
        }

        if (node.ParentId == 0)
        {
            error = "根节点不能剪枝";
            return false;
        }

        RewindNode current = _tree.Current;
        if (current != null && _tree.PathToRoot(current.Id).Contains(id))
        {
            error = "当前节点在该子树里，先跳到其它分支再剪";
            return false;
        }

        // 传送门不再阻止剪枝：删掉规范节点后由 RewindTree.Prune 重选主节点
        // （最深优先；只剩一个传送门时它直接转正）。这里只保留“当前节点不能在子树里”这条。
        return true;
    }

    /// <summary>
    /// 纯本地的剪枝动作：内存和磁盘一起删，然后落盘 + 通知 UI。
    /// 协同剪枝由房主/客户端在 Commit 阶段各自调用同一次确定性剪枝。
    /// </summary>
    internal bool PruneLocal(int id, out int removed, out string error)
    {
        removed = 0;

        if (!_tree.Prune(id, out removed, out List<int> removedIds, out error))
        {
            return false;
        }

        // 内存和磁盘必须一起剪：只删内存会在 nodes/ 下留孤儿摘要
        if (removedIds != null)
        {
            foreach (int removedId in removedIds)
            {
                _store.DeleteNode(removedId);
            }
        }

        _store.SaveTree(_tree);
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// [测试脚手架] 造一个传送门（不抓快照、不改当前节点），用来验证连线几何与剪枝重选。
    /// 只在 GLOBAL_REWIND_TEST_HARNESS=1 时由 MCP 暴露。
    /// </summary>
    internal bool AddTestPortal(int parentId, int targetId, out int portalId, out string error)
    {
        portalId = 0;

        int before = _tree.CurrentId;
        RewindNode portal = _tree.AddPortal(parentId, targetId, "测试传送门", "test", out error);
        _tree.CurrentId = before;

        if (portal == null)
        {
            return false;
        }

        portal.Playable = true;
        portal.Shared = !PlayerScope.IsMultiplayer;
        portal.State = "test";
        portalId = portal.Id;

        _store.SaveTree(_tree);
        NotifyChanged();
        return true;
    }

    /// <summary>
    /// [测试脚手架] 把节点上移 N 层（改挂到祖父下），用来制造“更深的同态节点”，
    /// 验证换主链路。只在 GLOBAL_REWIND_TEST_HARNESS=1 时由 MCP 暴露。
    /// </summary>
    internal bool ShrinkDepth(int id, int levels, out int oldDepth, out int newDepth, out string error)
    {
        error = null;
        oldDepth = _tree.Depth(id);

        RewindNode node = _tree.Get(id);
        if (node == null)
        {
            newDepth = oldDepth;
            error = $"节点 #{id} 不存在";
            return false;
        }

        levels = Math.Max(1, Math.Min(levels, 8));
        for (int i = 0; i < levels; i++)
        {
            RewindNode parent = _tree.Get(node.ParentId);
            RewindNode grand = parent == null ? null : _tree.Get(parent.ParentId);
            if (grand == null)
            {
                break;
            }

            parent.Children.Remove(node.Id);
            node.ParentId = grand.Id;
            grand.Children.Add(node.Id);
        }

        newDepth = _tree.Depth(id);
        _store.SaveTree(_tree);
        NotifyChanged();
        return newDepth != oldDepth;
    }

    /// <summary>
    /// 树的稳定指纹：当前节点 + 编号空间 + 所有节点的 (id, parent, checksum)。
    /// 多人剪枝前用它确认两端树**完全一致**，避免在一端删了另一端没有的子树。
    /// </summary>
    internal string TreeFingerprint()
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(_tree.CurrentId).Append('|').Append(_tree.NextId).Append('|');

        foreach (int id in _tree.Nodes.Keys.OrderBy(value => value))
        {
            RewindNode node = _tree.Get(id);
            if (node == null)
            {
                continue;
            }

            sb.Append(node.Id).Append(':').Append(node.ParentId).Append(':')
                .Append(node.Checksum.ToString("x8")).Append(':')
                .Append(node.AliasOf).Append(';');
        }

        // FNV-1a 64：不依赖任何外部哈希实现，跨端/跨版本结果稳定。
        ulong hash = 14695981039346656037UL;
        string text = sb.ToString();
        for (int i = 0; i < text.Length; i++)
        {
            hash ^= text[i];
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16");
    }

    /// <summary>
    /// 恢复后把"入队时产生、但恢复点不该保留"的瞬时标志清掉。
    ///
    /// 目前只有药水的 IsQueued（入队前置位、结算后游戏自己清）。
    ///
    /// CombatManager._playerActionsDisabled 与 PaelsEye 额外回合的真值都由快照精确恢复
    /// （Roots + MementoRules 字段白名单），这里**不能**再按 IsPlayerReadyToEndTurn 或
    /// "是否轮到玩家"重算 —— 那会让运行态偏离节点，直接破坏"跳转后与节点逐字节一致"，
    /// DeepCompare 会把这个字段核进去。
    /// </summary>
    private static void NormalizeAfterRestore()
    {
        try
        {
            IRunState state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null)
            {
                return;
            }

            PropertyInfo isQueued = typeof(PotionModel).GetProperty(
                "IsQueued", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (isQueued != null && isQueued.CanWrite)
            {
                foreach (Player player in state.Players)
                {
                    foreach (PotionModel potion in player.Potions)
                    {
                        if (potion != null && potion.IsQueued)
                        {
                            isQueued.SetValue(potion, false);
                        }
                    }
                }
            }

            // 队列/执行器的暂停是传输态，不进快照；稳定点本来就该是“未暂停”。
            // 这里幂等复位一次，避免恢复后牌打不出去（多人局尤其容易被对端的
            // EndTurnPhaseOne / NotPlayPhase 残留卡住）。
            RunManager manager = RunManager.Instance;
            manager?.ActionExecutor?.Unpause();
            manager?.ActionQueueSet?.UnpauseAllPlayerQueues();

            // 多人局：choice / hook 的在途记录不进快照，写回后必须清空。
            // 不清的话，旧 choice id 会命中上一次（写回前）留下的等待者，
            // 轻则动作永远挂起，重则把结果投递到已经不存在的那次选择上。
            if (PlayerScope.IsMultiplayer)
            {
                TurnStateProbe.ClearCollection(manager?.PlayerChoiceSynchronizer, "_receivedChoices");
                TurnStateProbe.ClearCollection(manager?.ActionQueueSynchronizer, "_hookActions");
                TurnStateProbe.ClearCollection(
                    manager?.ActionQueueSynchronizer, "_requestedActionsWaitingForPlayerTurn");
            }

            // 注意：CombatManager._playerActionsDisabled 已由快照精确恢复（见 Roots + MementoRules
            // 白名单），这里**不能**再按 IsPlayerReadyToEndTurn 重算 —— 那会让"运行态"偏离节点，
            // 直接破坏"跳转后与节点逐字节一致"。DeepCompare 会把这个字段也核进去。
            // 额外回合同理：不能靠"是否是玩家回合"猜，快照里存的就是捕获那一刻的真值。
        }
        catch (Exception e)
        {
            ModLog.Warn($"normalize failed: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 动作描述

    // 注意：这里刻意没有 IsTrackedAction 白名单。所有 GameAction 都从
    // ActionQueueSet.ActionEnqueued 进入，Describe / KindOf 的 default 分支
    // 负责未知类型。漏掉任何一类动作都会让"全事件记录"不成立。

    private static string Describe(GameAction action)
    {
        switch (action)
        {
            case PlayCardAction play:
                return $"打出 {play.CardModelId.Entry}";
            case UsePotionAction potion:
                return $"使用药水 #{potion.PotionIndex}";
            case DiscardPotionGameAction:
                return "丢弃药水";
            case EndPlayerTurnAction:
                return "结束回合";
            case UndoEndPlayerTurnAction:
                return "撤销结束回合";
            case GenericHookGameAction hook:
                return $"结算 hook #{hook.HookId}";
            default:
                return action.GetType().Name;
        }
    }

    private static string KindOf(GameAction action)
    {
        switch (action)
        {
            case PlayCardAction:
                return "card";
            case UsePotionAction:
                return "potion";
            case DiscardPotionGameAction:
                return "potion_discard";
            case EndPlayerTurnAction:
                return "end_turn";
            case UndoEndPlayerTurnAction:
                return "undo_end_turn";
            case GenericHookGameAction:
                return "hook";
            default:
                return "action";
        }
    }
}
