using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.App;
using GlobalRewind.Logging;
using GlobalRewind.Players;
using GlobalRewind.Snapshot;
using GlobalRewind.Tree;

namespace GlobalRewind.Net;

internal enum JumpPhase
{
    None,
    /// <summary>已冻结，等本机静止。</summary>
    Freezing,
    /// <summary>本机已静止（房主也在这里等客户端）。</summary>
    Idle,
    /// <summary>客户端：已静止，等房主提交。</summary>
    WaitCommit,
    /// <summary>客户端：已写回，等房主 Resume/Abort。</summary>
    WaitResume,
    /// <summary>客户端：本端失败（已 nack），等房主收尾。</summary>
    WaitAbort,
    /// <summary>房主：已广播 Commit，等客户端恢复确认。</summary>
    WaitRestored,
    Done
}

internal enum PrunePhase
{
    None,
    /// <summary>房主：已广播 Prepare，等各端 ready。</summary>
    WaitReady,
    /// <summary>客户端：已 ready，等房主 Commit/Abort。</summary>
    WaitCommit,
    /// <summary>房主：已广播 Commit，等各端 Done。</summary>
    WaitDone,
    Done
}

/// <summary>
/// M2：多人协同跳转。
///
/// 两条职责：
///   1) **节点镜像**：真联机里节点 id 由房主分配，房主建点后广播锚点；
///      客户端在同一个 checksum + frontier 上抓自己的 memento，再回 ack。
///      房主收齐全员 ack 才把节点标成 shared（可跳），否则 partial（不可跳）。
///   2) **协同跳转**：房主权威。
///        Freeze → 各端禁输入并等本机静止
///        Idle   → 各端回报 checksum + frontier，房主先核对再动手
///        Commit → 房主写回成功后才让各端写回；各端各自 Verify + 纯哈希
///        Resume → 全员一致才重建画面、解冻；任一端失败则 Abort + 全员回滚
///
/// 所有游戏对象访问都在主线程：网络回调只把消息塞进队列，由
/// <see cref="Pump"/>（SceneTree.ProcessFrame）统一推进状态机。
/// </summary>
internal sealed class RewindCoordinator
{
    public static RewindCoordinator Instance { get; } = new RewindCoordinator();

    private const int AnchorTimeoutMs = 4000;
    private const int JumpIdleTimeoutMs = 8000;
    private const int JumpRestoreTimeoutMs = 10000;
    private const int PruneTimeoutMs = 5000;
    private const int MirrorCacheLimit = 6;
    private const int MirrorCacheTtlMs = 10000;
    private const int HelloRetryMs = 1500;
    private const int HelloMaxAttempts = 4;

    private readonly RewindNetChannel _net = new RewindNetChannel();
    private readonly ConcurrentQueue<Incoming> _inbox = new ConcurrentQueue<Incoming>();
    private readonly Dictionary<ulong, PeerState> _peers = new Dictionary<ulong, PeerState>();
    private readonly List<PendingAnchor> _pendingAnchors = new List<PendingAnchor>();
    private readonly Dictionary<int, PendingNode> _pendingNodes = new Dictionary<int, PendingNode>();

    // 客户端收到过的锚点父链（id → 房主那边记的父 id）。
    // 正常路径用不到：只有某个锚点没能落地时，才靠它把后来的子孙挂到最近的已落地祖先上，
    // 避免“父节点缺失 → 整棵子树在 UI 里彻底看不见”。
    private readonly Dictionary<int, int> _anchorParents = new Dictionary<int, int>();

    // 客户端在“动作逻辑边界”抓的镜像快照：房主锚点稍后才到，
    // 这时候本端可能已经在执行下一个动作了，实时抓点必然对不上。
    // 缓存按 checksum 命中，命中即用、用完即删；只保留最近几条。
    private readonly List<CachedMirror> _mirrorCache = new List<CachedMirror>(MirrorCacheLimit);

    private bool _active;
    private int _combatIndex;
    private ulong _nextHelloAtMs;
    private int _helloAttempts;
    private string _lastHelloError;

    private JumpSession _session;
    private LocalFreeze _freeze;
    private JumpResult _lastResult;
    private PruneSession _prune;
    private PruneResult _lastPrune;
    private bool _pruneGuard;
    private int _nextHostRequestId = 1;
    private int _nextClientRequestId = 1;
    private int _nextPruneRequestId = 1;

    // ---------------------------------------------------------------- 公共状态

    public bool IsActive => _active;

    public bool IsHost => _net.IsHost;

    public bool IsClient => _net.IsClient;

    public bool InSession => _session != null || _freeze != null || _prune != null;

    public JumpResult LastResult => _lastResult;

    public PruneResult LastPrune => _lastPrune;

    public string LastHelloError => _lastHelloError;

    public int CombatIndex => _combatIndex;

    // 测试钩子：下一次 ApplyMpRestore 成功后立即“判定失败”，用于验证 Abort + 回滚链路。
    private bool _poisonNextRestore;

    public bool PoisonNextRestore()
    {
        _poisonNextRestore = true;
        return true;
    }

    public bool ConsumePoisonedRestore()
    {
        if (!_poisonNextRestore)
        {
            return false;
        }

        _poisonNextRestore = false;
        ModLog.Warn("mp test hook: next restore will be reported as failed");
        return true;
    }

    /// <summary>冻结/恢复期间要挡住本地抓点与手动跳转。</summary>
    public bool IsBusy => _freeze != null || _prune != null;

    // ---------------------------------------------------------------- 生命周期

    public void OnRunStarted()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager?.NetService == null)
            {
                return;
            }

            bool registered = _net.Ensure(manager.NetService, Enqueue);
            if (registered)
            {
                _combatIndex = 0;
                _peers.Clear();
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp OnRunStarted failed: {e.Message}");
        }
    }

    public void OnCombatBegan()
    {
        try
        {
            RunManager manager = RunManager.Instance;
            if (manager?.NetService == null)
            {
                return;
            }

            _net.Ensure(manager.NetService, Enqueue);

            if (!_net.IsMultiplayer)
            {
                _active = false;
                return;
            }

            _active = true;
            _combatIndex++;
            _peers.Clear();
            _pendingNodes.Clear();
            ClearMirrorCache();
            _session = null;
            _freeze = null;
            _lastResult = null;
            _prune = null;
            _pruneGuard = false;
            _lastPrune = null;
            _lastHelloError = null;
            _helloAttempts = 0;
            _nextHelloAtMs = 0;

            // 锚点可能比本端的 CombatBegan 更早到（房主先一步抓了根节点，本端还在加载战斗房间）：
            // **不能清空**。根节点的锚点一旦丢掉，房主后续所有节点的 parent 在本端都不存在，
            // 建点会全部失败，客户端整棵回溯树永远是空的（只剩一句“本场战斗还没有节点”）。
            // 上一场战斗残留的锚点在 OnCombatEnded / OnRunCleanUp 里已经清过，这里只重排父链、
            // 把重试窗口从现在开始重新计时。
            _anchorParents.Clear();
            foreach (PendingAnchor pending in _pendingAnchors)
            {
                _anchorParents[pending.Message.NodeId] = pending.Message.ParentId;
            }

            int keptAnchors = _pendingAnchors.Count;
            ulong now = Now;
            foreach (PendingAnchor pending in _pendingAnchors)
            {
                pending.DeadlineMs = now + AnchorTimeoutMs;
            }

            ModLog.Info($"mp coordinator ready (role={(_net.IsHost ? "host" : "client")}, "
                        + $"local={PlayerScope.LocalNetId}, peers={_net.Peers().Count}, "
                        + $"kept_anchors={keptAnchors})");

            if (keptAnchors > 0)
            {
                RewindRuntime.Instance.LogMpEvent("mp_anchor_kept",
                    $"{keptAnchors} 个锚点早于本端 CombatBegan 到达，转入本场战斗重试", 0);
            }

            if (_net.IsHost)
            {
                _helloAttempts = 1;
                _nextHelloAtMs = Now + HelloRetryMs;
                SendHello();
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp OnCombatBegan failed: {e.Message}");
        }
    }

    public void OnCombatEnded()
    {
        try
        {
            if (_freeze != null)
            {
                AbortLocal("战斗结束，跳转取消");
            }

            if (_prune != null)
            {
                AbortPrune("战斗结束，剪枝取消");
            }

            _session = null;
            _active = false;
            _pendingAnchors.Clear();
            _pendingNodes.Clear();
            _anchorParents.Clear();
            ClearMirrorCache();
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp OnCombatEnded failed: {e.Message}");
        }
    }

    public void OnRunCleanUp()
    {
        try
        {
            if (_freeze != null)
            {
                AbortLocal("本局结束，跳转取消");
            }

            if (_prune != null)
            {
                AbortPrune("本局结束，剪枝取消");
            }

            _session = null;
            _active = false;
            _peers.Clear();
            _pendingAnchors.Clear();
            _pendingNodes.Clear();
            _anchorParents.Clear();
            ClearMirrorCache();
            _net.Unregister();
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp OnRunCleanUp failed: {e.Message}");
        }
        finally
        {
            _active = false;
        }
    }

    // ---------------------------------------------------------------- 帧驱动

    /// <summary>由 SceneTree.ProcessFrame 驱动（主线程）。</summary>
    public void Pump()
    {
        if (!_active)
        {
            return;
        }

        if (_net.IsClient && _mirrorCache.Count > 0)
        {
            TrimMirrorCache(Now);
        }

        DrainInbox();
        PumpHello();
        PumpAnchors();
        PumpPendingNodes();
        PumpLocalFreeze();
        PumpHostSession();
        PumpPrune();
    }

    // ---------------------------------------------------------------- 握手

    private void SendHello()
    {
        CombatChecksum.ComputePure(out uint checksum);
        _net.SendToAll(new GrHelloMessage
        {
            Protocol = RewindNetProtocol.Version,
            ModVersion = RewindNetProtocol.ModVersion,
            CombatIndex = _combatIndex,
            RootChecksum = checksum
        });
    }

    /// <summary>握手重试：客户端可能在战斗刚开始的那几帧还没挂好处理器。</summary>
    private void PumpHello()
    {
        if (!_net.IsHost || _helloAttempts >= HelloMaxAttempts)
        {
            return;
        }

        ulong now = Now;
        if (now < _nextHelloAtMs)
        {
            return;
        }

        _helloAttempts++;
        SendHello();
        _nextHelloAtMs = now + HelloRetryMs;
    }

    // ---------------------------------------------------------------- 客户端边界快照缓存

    /// <summary>
    /// 客户端在动作逻辑边界抓的镜像快照入缓存。
    /// 房主锚点带着同一个 checksum 到达时直接取用，不再要求“本端此刻正好静止”。
    /// </summary>
    internal void StashMirrorBoundary(uint checksum, Frontier frontier, ObjectGraphSnapshot snapshot)
    {
        if (!_active || !_net.IsClient || snapshot == null || checksum == 0)
        {
            return;
        }

        ulong now = Now;
        TrimMirrorCache(now);

        _mirrorCache.RemoveAll(entry => entry.Checksum == checksum);
        _mirrorCache.Add(new CachedMirror
        {
            Checksum = checksum,
            Frontier = frontier,
            Snapshot = snapshot,
            CreatedMs = now
        });

        while (_mirrorCache.Count > MirrorCacheLimit)
        {
            _mirrorCache.RemoveAt(0);
        }

        RewindRuntime.Instance.LogMpEvent("mp_mirror_cached",
            $"checksum={checksum:x8} cache={_mirrorCache.Count} objects={snapshot.ObjectCount}",
            0);
    }

    private bool TryTakeCachedMirror(GrAnchorMessage anchor, out ObjectGraphSnapshot snapshot,
        out uint checksum, out Frontier frontier)
    {
        snapshot = null;
        checksum = 0;
        frontier = null;

        TrimMirrorCache(Now);

        for (int i = 0; i < _mirrorCache.Count; i++)
        {
            CachedMirror entry = _mirrorCache[i];
            if (entry.Checksum != anchor.Checksum)
            {
                continue;
            }

            if (anchor.Frontier != null && entry.Frontier != null
                && !anchor.Frontier.SameAs(entry.Frontier))
            {
                continue;
            }

            snapshot = entry.Snapshot;
            checksum = entry.Checksum;
            frontier = entry.Frontier;
            _mirrorCache.RemoveAt(i);
            return true;
        }

        return false;
    }

    private void TrimMirrorCache(ulong now)
    {
        for (int i = _mirrorCache.Count - 1; i >= 0; i--)
        {
            if (now - _mirrorCache[i].CreatedMs > MirrorCacheTtlMs)
            {
                _mirrorCache.RemoveAt(i);
            }
        }
    }

    private void ClearMirrorCache()
    {
        _mirrorCache.Clear();
    }

    // ---------------------------------------------------------------- 节点镜像

    /// <summary>
    /// 房主建点后调用：广播锚点并等待各端确认。
    /// demoteOldId != 0 表示这个新节点接管了旧节点的规范身份（换主），
    /// 客户端绑定新节点后按同一顺序把旧主的子树搬过来。
    /// </summary>
    public void OnHostNodeCreated(RewindNode node, int demoteOldId = 0)
    {
        try
        {
            if (!_active || !_net.IsHost || node == null)
            {
                return;
            }

            List<ulong> peers = _net.Peers();
            if (peers.Count == 0)
            {
                node.Shared = true;
                node.State = "shared";
                RewindRuntime.Instance.UpdateNodeSyncState(node);
                return;
            }

            node.Shared = false;
            node.State = "pending";
            RewindRuntime.Instance.UpdateNodeSyncState(node);

            var pending = new PendingNode
            {
                NodeId = node.Id,
                Checksum = node.Checksum,
                DeadlineMs = Now + AnchorTimeoutMs
            };

            foreach (ulong peer in peers)
            {
                pending.Waiting.Add(peer);
            }

            _pendingNodes[node.Id] = pending;

            _net.SendToAll(new GrAnchorMessage
            {
                Protocol = RewindNetProtocol.Version,
                NodeId = node.Id,
                ParentId = node.ParentId,
                Label = node.Label,
                Kind = node.Kind,
                Checksum = node.Checksum,
                Playable = node.Playable,
                Force = node.Kind == "combat_start",
                Round = node.Round,
                Turn = node.Turn,
                Participants = node.Participants ?? Array.Empty<ulong>(),
                Frontier = node.Frontier,
                AliasOf = node.AliasOf,
                DemoteOldId = demoteOldId
            });

            RewindRuntime.Instance.LogMpEvent("mp_anchor",
                $"node #{node.Id} checksum={node.Checksum:x8} waiting={pending.Waiting.Count}", node.Id);
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp OnHostNodeCreated failed: {e.Message}");
        }
    }

    private void PumpAnchors()
    {
        if (!_net.IsClient || _pendingAnchors.Count == 0)
        {
            return;
        }

        ulong now = Now;
        int index = 0;
        while (index < _pendingAnchors.Count)
        {
            PendingAnchor pending = _pendingAnchors[index];

            if (TryBindAnchor(pending.Message, out string reason))
            {
                _pendingAnchors.RemoveAt(index);
                continue;
            }

            pending.LastReason = reason;
            if (now < pending.DeadlineMs)
            {
                index++;
                continue;
            }

            // 超时：仍然占住这个 id，标成 partial（不可跳），保证两端编号空间一致。
            // 父节点在本端缺失时不能直接放弃——那会让整棵子树在 UI 里连根都走不到。
            int attachTo = ResolveAttachParent(pending.Message);
            string failed = $"锚点 #{pending.Message.NodeId} 未能在本端复现：{reason}";
            RewindRuntime.Instance.AddMirroredNode(
                pending.Message.NodeId,
                attachTo,
                pending.Message.Label,
                pending.Message.Kind,
                pending.Message.Frontier,
                pending.Message.Checksum,
                null,
                playable: false,
                shared: false,
                state: "partial",
                out string error,
                aliasOf: pending.Message.AliasOf);
            SendAnchorAck(pending.Message.NodeId, false, 0, failed);
            RewindRuntime.Instance.LogMpEvent("mp_anchor_failed", failed, pending.Message.NodeId);
            _pendingAnchors.RemoveAt(index);
        }
    }

    /// <summary>
    /// 锚点的父节点在本端不存在时，沿“收到过的锚点父链”找最近的已落地祖先。
    /// 找不到（父链也断了）就挂到现有的根上；树还是空的时候就让它自己当根。
    ///
    /// 这条路径只在同步已经出问题时才会走到：正常收到锚点的顺序与房主建点顺序一致，
    /// 父节点一定先到。宁可把节点摆在一个可诊断的位置上（两端树指纹会不一致，剪枝会明确拒绝），
    /// 也不要让整棵子树在 UI 里凭空消失。
    /// </summary>
    private int ResolveAttachParent(GrAnchorMessage anchor)
    {
        RewindTree tree = RewindRuntime.Instance.Tree;
        int parentId = anchor.ParentId;
        if (parentId == 0 || tree.Get(parentId) != null)
        {
            return parentId;
        }

        int cursor = parentId;
        int guard = 0;
        while (cursor != 0 && guard++ < 64)
        {
            if (tree.Get(cursor) != null)
            {
                RewindRuntime.Instance.LogMpEvent("mp_anchor_orphan",
                    $"锚点 #{anchor.NodeId} 的父节点 #{anchor.ParentId} 不在本端，改挂到 #{cursor}",
                    anchor.NodeId);
                return cursor;
            }

            if (!_anchorParents.TryGetValue(cursor, out int next))
            {
                break;
            }

            cursor = next;
        }

        int rootId = tree.Root?.Id ?? 0;
        RewindRuntime.Instance.LogMpEvent("mp_anchor_orphan",
            rootId != 0
                ? $"锚点 #{anchor.NodeId} 的整条父链都不在本端，改挂到根 #{rootId}"
                : $"锚点 #{anchor.NodeId} 的整条父链都不在本端，本端还没有根：它为 root 占位",
            anchor.NodeId);
        return rootId;
    }

    private bool TryBindAnchor(GrAnchorMessage anchor, out string reason)
    {
        reason = null;

        RewindRuntime runtime = RewindRuntime.Instance;
        RewindNode existing = runtime.Tree.Get(anchor.NodeId);
        if (existing != null)
        {
            AckExistingMirror(existing);
            return true;
        }

        bool force = anchor.Force;
        ObjectGraphSnapshot snapshot;
        uint checksum;
        Frontier frontier;

        if (!force && TryTakeCachedMirror(anchor, out snapshot, out checksum, out frontier))
        {
            RewindRuntime.Instance.LogMpEvent("mp_anchor_cache_hit",
                $"node #{anchor.NodeId} checksum={checksum:x8}", anchor.NodeId);
        }
        else if (!runtime.TryCaptureMirrorPoint(
                     anchor.Checksum,
                     anchor.Frontier,
                     force,
                     out snapshot,
                     out checksum,
                     out frontier,
                     out reason))
        {
            return false;
        }

        // 校验和必须在建点之前核对：先建点再判失败会留下一个 checksum 与房主不同的镜像节点，
        // 后续重试只会命中“已存在”分支把它 ack 成 ok，错误就此固化下来。
        if (!force && anchor.Checksum != 0 && checksum != anchor.Checksum)
        {
            reason = $"checksum 不一致（房主 {anchor.Checksum:x8} / 本端 {checksum:x8}）";
            return false;
        }

        RewindNode node = runtime.AddMirroredNode(
            anchor.NodeId,
            anchor.ParentId,
            anchor.Label,
            anchor.Kind,
            frontier,
            checksum,
            snapshot,
            playable: anchor.Playable,
            shared: true,
            state: "shared",
            out string error,
            aliasOf: anchor.AliasOf);

        if (node == null)
        {
            reason = error ?? "建点失败";
            return false;
        }

        // 换主：客户端在本地重放同一操作（两端树一致，搬家结果确定）。
        if (anchor.DemoteOldId != 0)
        {
            if (!runtime.Tree.DemoteCanonical(anchor.DemoteOldId, node.Id,
                    out int movedChildren, out string demoteError))
            {
                reason = $"换主失败：{demoteError}";
                return false;
            }

            runtime.LogMpEvent("mp_canonical",
                $"node #{node.Id} took canon from #{anchor.DemoteOldId}, moved={movedChildren}", node.Id);
        }

        SendAnchorAck(anchor.NodeId, true, checksum, null);
        return true;
    }

    private void SendAnchorAck(int nodeId, bool ok, uint checksum, string reason)
    {
        _net.SendToHost(new GrAnchorAckMessage
        {
            NodeId = nodeId,
            Ok = ok,
            Checksum = checksum,
            Reason = reason ?? string.Empty
        });
    }

    /// <summary>
    /// 锚点指向的节点已经镜像过：回报本端的真实状态。
    /// partial（没抓成快照）必须报 ok=false —— 否则房主会把它标成 shared/可跳，
    /// 而本端根本没有可写回的 memento，跳过去必然失败。重复投递的锚点也走这里。
    /// </summary>
    private void AckExistingMirror(RewindNode node)
    {
        bool ok = node.Snapshot != null
                  && !string.Equals(node.State, "partial", StringComparison.Ordinal);
        SendAnchorAck(node.Id, ok, node.Checksum, ok ? null : $"本端节点 #{node.Id} 是 {node.State}");
    }

    private void OnAnchorAck(ulong senderId, GrAnchorAckMessage message)
    {
        if (!_pendingNodes.TryGetValue(message.NodeId, out PendingNode pending))
        {
            return;
        }

        pending.Waiting.Remove(senderId);
        pending.Acks[senderId] = message;

        if (!message.Ok || pending.Waiting.Count == 0)
        {
            FinalizePendingNode(pending);
        }
    }

    private void PumpPendingNodes()
    {
        if (!_net.IsHost || _pendingNodes.Count == 0)
        {
            return;
        }

        ulong now = Now;
        var expired = new List<PendingNode>();
        foreach (PendingNode pending in _pendingNodes.Values)
        {
            if (now >= pending.DeadlineMs)
            {
                expired.Add(pending);
            }
        }

        foreach (PendingNode pending in expired)
        {
            pending.Timeout = true;
            FinalizePendingNode(pending);
        }
    }

    private void FinalizePendingNode(PendingNode pending)
    {
        _pendingNodes.Remove(pending.NodeId);

        RewindNode node = RewindRuntime.Instance.Tree.Get(pending.NodeId);
        if (node == null)
        {
            return;
        }

        bool allOk = !pending.Timeout && pending.Acks.Count > 0;
        string failure = pending.Timeout ? "等待对端锚点确认超时" : null;
        bool force = string.Equals(node.Kind, "combat_start", StringComparison.Ordinal);

        foreach (KeyValuePair<ulong, GrAnchorAckMessage> pair in pending.Acks)
        {
            GrAnchorAckMessage ack = pair.Value;
            if (!ack.Ok)
            {
                allOk = false;
                failure ??= $"{pair.Key}: {ack.Reason}";
            }
            else if (!force && ack.Checksum != node.Checksum)
            {
                allOk = false;
                failure ??= $"{pair.Key}: checksum {ack.Checksum:x8} != {node.Checksum:x8}";
            }
        }

        node.Shared = allOk;
        node.State = allOk ? "shared" : "partial";
        if (!allOk)
        {
            node.Playable = false;
        }

        RewindRuntime.Instance.UpdateNodeSyncState(node);
        RewindRuntime.Instance.LogMpEvent(
            allOk ? "mp_anchor_shared" : "mp_anchor_partial",
            allOk
                ? $"node #{node.Id} 全端一致"
                : $"node #{node.Id} 只有部分端持有：{failure}",
            node.Id);

        if (!allOk)
        {
            ModLog.Warn($"mp anchor #{node.Id} partial: {failure}");
        }
    }

    // ---------------------------------------------------------------- 跳转入口

    internal enum JumpStart
    {
        Rejected,
        HostStarted,
        ClientRequested
    }

    internal JumpStart RequestJump(int nodeId, out string error, out int requestId)
    {
        error = null;
        requestId = 0;

        if (!_active)
        {
            error = "多人协同未就绪（不在战斗中或网络未连接）";
            return JumpStart.Rejected;
        }

        if (InSession)
        {
            error = "已有一个协同跳转在进行中";
            return JumpStart.Rejected;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        RewindNode node = runtime.Tree.Get(nodeId);
        if (node == null)
        {
            error = $"节点 #{nodeId} 不存在";
            return JumpStart.Rejected;
        }

        if (!node.Shared || node.State == "partial" || node.State == "pending")
        {
            error = $"节点 #{nodeId} 还没被所有参与者确认（state={node.State}）";
            return JumpStart.Rejected;
        }

        if (!node.Playable)
        {
            error = $"节点 #{nodeId} 不是可操作状态";
            return JumpStart.Rejected;
        }

        if (node.Snapshot == null)
        {
            error = $"节点 #{nodeId} 没有内存快照";
            return JumpStart.Rejected;
        }

        if (node.Participants != null && node.Participants.Length > 0)
        {
            ulong[] roster = PlayerScope.All
                .Where(player => player != null)
                .Select(player => player.NetId)
                .OrderBy(id => id)
                .ToArray();

            if (!node.Participants.OrderBy(id => id).SequenceEqual(roster))
            {
                error = $"节点 #{nodeId} 的参与者与当前玩家名单不一致（stale）";
                node.State = "stale";
                node.Shared = false;
                node.Playable = false;
                runtime.UpdateNodeSyncState(node);
                return JumpStart.Rejected;
            }
        }

        if (_net.IsHost)
        {
            requestId = _nextHostRequestId++;
            if (!StartHostSession(node, requestId, 0, out error))
            {
                return JumpStart.Rejected;
            }

            return JumpStart.HostStarted;
        }

        // 客户端：只发请求，由房主决定并广播 Freeze。
        requestId = _nextClientRequestId++;
        _net.SendToHost(new GrJumpRequestMessage { RequestId = requestId, NodeId = nodeId });
        _lastResult = new JumpResult
        {
            Pending = true,
            Ok = false,
            RequestId = requestId,
            NodeId = nodeId,
            ExpectedChecksum = node.Checksum,
            StartedMs = Now,
            Phase = "requested"
        };
        runtime.LogMpEvent("mp_jump_request", $"向房主请求跳到 #{nodeId}", nodeId);
        return JumpStart.ClientRequested;
    }

    private bool StartHostSession(RewindNode node, int requestId, ulong requester, out string error)
    {
        error = null;

        if (!CheckPeersProtocol(out error))
        {
            return false;
        }

        List<ulong> peers = _net.Peers();
        RewindNode current = RewindRuntime.Instance.Tree.Current;
        var session = new JumpSession
        {
            RequestId = requestId,
            NodeId = node.Id,
            ExpectedChecksum = node.Checksum,
            ExpectedFrontier = node.Frontier,
            // Idle 阶段核对的是“跳转前大家是不是同一个状态”，不是目标节点：
            // 各端此刻都还停在当前节点上，所以基准取 Tree.Current。
            IdleChecksum = current?.Checksum ?? 0,
            IdleFrontier = current?.Frontier,
            Requester = requester,
            Phase = JumpPhase.Idle,
            DeadlineMs = Now + JumpIdleTimeoutMs
        };

        foreach (ulong peer in peers)
        {
            session.PendingIdle.Add(peer);
        }

        _session = session;
        BeginLocalFreeze(node.Id, requestId, node.Checksum, JumpIdleTimeoutMs);

        _net.SendToAll(new GrFreezeMessage
        {
            Protocol = RewindNetProtocol.Version,
            RequestId = requestId,
            NodeId = node.Id,
            ExpectedChecksum = node.Checksum,
            TimeoutMs = JumpIdleTimeoutMs
        });

        _lastResult = new JumpResult
        {
            Pending = true,
            RequestId = requestId,
            NodeId = node.Id,
            ExpectedChecksum = node.Checksum,
            StartedMs = Now,
            Phase = "freezing"
        };

        RewindRuntime.Instance.LogMpEvent("mp_freeze",
            $"req={requestId} target=#{node.Id} checksum={node.Checksum:x8} peers={peers.Count}", node.Id);
        return true;
    }

    // ---------------------------------------------------------------- 多人剪枝

    internal enum PruneStart
    {
        Rejected,
        HostStarted
    }

    /// <summary>
    /// 多人剪枝入口。只有房主能发起；客户端点了直接拒绝。
    ///
    /// 这不是“改战斗状态”，但仍然要求全体静止点：剪枝准备到提交之间只要有人出了动作，
    /// 就会在两边各建一个新节点，两端的树会立刻错位。所以流程是
    ///   Prepare → 各端本地校验并回 ready（带树指纹）→ 全端一致才 Commit → 各端确定性剪枝 → Done。
    /// 期间短暂挡住输入并禁止抓点，完成后立即恢复。
    /// </summary>
    internal PruneStart RequestPrune(int targetId, out string error, out int requestId)
    {
        error = null;
        requestId = 0;

        if (!_active)
        {
            error = "多人协同未就绪（不在战斗中或网络未连接）";
            return PruneStart.Rejected;
        }

        if (!_net.IsHost)
        {
            error = "多人局只有房主可以剪枝";
            return PruneStart.Rejected;
        }

        if (InSession)
        {
            error = "已有协同操作（跳转/剪枝）在进行中";
            return PruneStart.Rejected;
        }

        if (RewindRuntime.Instance.IsBusy)
        {
            error = "有动作正在执行，等结算完再剪枝";
            return PruneStart.Rejected;
        }

        if (_pendingNodes.Count > 0)
        {
            error = "还有节点在等对端确认，稍后再剪";
            return PruneStart.Rejected;
        }

        if (!QuiescenceProbe.TryCapture(out _, out string reason))
        {
            error = $"请在全体静止点剪枝：{reason}";
            return PruneStart.Rejected;
        }

        if (!IsActionQueueEmpty())
        {
            error = "还有动作在队列里没结算";
            return PruneStart.Rejected;
        }

        if (!RewindRuntime.Instance.CanPruneTarget(targetId, out error))
        {
            return PruneStart.Rejected;
        }

        requestId = _nextPruneRequestId++;
        var session = new PruneSession
        {
            RequestId = requestId,
            TargetId = targetId,
            Phase = PrunePhase.WaitReady,
            DeadlineMs = Now + PruneTimeoutMs
        };

        List<ulong> peers = _net.Peers();
        foreach (ulong peer in peers)
        {
            session.PendingReady.Add(peer);
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        _lastPrune = new PruneResult
        {
            Pending = true,
            RequestId = requestId,
            TargetId = targetId,
            NodeCountBefore = runtime.Tree.Count,
            StartedMs = Now,
            Phase = "prepare"
        };

        _prune = session;
        BeginPruneGuard();

        _net.SendToAll(new GrPrunePrepareMessage
        {
            Protocol = RewindNetProtocol.Version,
            PruneId = requestId,
            TargetId = targetId
        });

        runtime.LogMpEvent("mp_prune_prepare",
            $"req={requestId} target=#{targetId} peers={peers.Count} nodes={runtime.Tree.Count}",
            targetId);

        if (session.PendingReady.Count == 0)
        {
            CommitPrune();
        }

        return PruneStart.HostStarted;
    }

    /// <summary>剪枝期间的双保险：禁输入 + 暂停队列 + 禁止抓点。完成后必须 ReleasePruneGuard。</summary>
    private void BeginPruneGuard()
    {
        if (_pruneGuard)
        {
            return;
        }

        _pruneGuard = true;
        RewindRuntime.Instance.SetCaptureSuspended(true);
        SetInputBlocked(true);
        PauseQueues();
    }

    private void ReleasePruneGuard()
    {
        if (!_pruneGuard)
        {
            return;
        }

        _pruneGuard = false;
        RewindRuntime.Instance.SetCaptureSuspended(false);
        UnpauseQueues();
        SetInputBlocked(false);
        RewindRuntime.Instance.NotifyChangedPublic();
    }

    private void PumpPrune()
    {
        if (_prune == null)
        {
            return;
        }

        ulong now = Now;

        if (_prune.Phase == PrunePhase.WaitReady)
        {
            if (now >= _prune.DeadlineMs)
            {
                AbortPrune("等待其他玩家确认超时");
            }

            return;
        }

        if (_prune.Phase == PrunePhase.WaitCommit)
        {
            if (now >= _prune.DeadlineMs)
            {
                AbortLocalPrune("等待房主提交剪枝超时");
            }

            return;
        }

        if (_prune.Phase == PrunePhase.WaitDone)
        {
            if (_prune.PendingDone.Count == 0)
            {
                FinishPrune(null);
                return;
            }

            if (now >= _prune.DeadlineMs)
            {
                // 已经剪完并落了盘，不能再回滚；只记录告警，让调用方看到“有端没确认”。
                FinishPrune("等待对端剪枝确认超时（本端已执行）");
            }
        }
    }

    private void CommitPrune()
    {
        if (_prune == null)
        {
            return;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        int currentId = runtime.Tree.CurrentId;
        int nodeCount = runtime.Tree.Count;
        string fingerprint = runtime.TreeFingerprint();

        string mismatch = null;
        foreach (KeyValuePair<ulong, GrPruneReadyMessage> pair in _prune.Ready)
        {
            GrPruneReadyMessage ack = pair.Value;
            if (!ack.Ok)
            {
                mismatch ??= $"{pair.Key}: {ack.Reason}";
                continue;
            }

            if (ack.CurrentId != currentId)
            {
                mismatch ??= $"{pair.Key}: 当前节点 {ack.CurrentId} != {currentId}";
            }
            else if (ack.NodeCount != nodeCount || !string.Equals(ack.TreeHash, fingerprint, StringComparison.Ordinal))
            {
                mismatch ??= $"{pair.Key}: 树结构不一致（nodes {ack.NodeCount}/{nodeCount}, hash {ack.TreeHash}/{fingerprint}）";
            }
        }

        if (mismatch != null)
        {
            AbortPrune("两端树不一致：" + mismatch);
            return;
        }

        if (!runtime.PruneLocal(_prune.TargetId, out int removed, out string error))
        {
            AbortPrune("房主剪枝失败：" + error);
            return;
        }

        _prune.Phase = PrunePhase.WaitDone;
        _prune.PendingDone.Clear();
        foreach (ulong peer in _net.Peers())
        {
            _prune.PendingDone.Add(peer);
        }

        _prune.DeadlineMs = Now + PruneTimeoutMs;

        _net.SendToAll(new GrPruneCommitMessage
        {
            PruneId = _prune.RequestId,
            TargetId = _prune.TargetId
        });

        if (_lastPrune != null)
        {
            _lastPrune.Removed = removed;
            _lastPrune.NodeCountAfter = runtime.Tree.Count;
            _lastPrune.Phase = "commit";
        }

        runtime.LogMpEvent("mp_prune_commit",
            $"req={_prune.RequestId} target=#{_prune.TargetId} removed={removed} "
            + $"nodes={runtime.Tree.Count} hash={runtime.TreeFingerprint()}",
            _prune.TargetId);

        if (_prune.PendingDone.Count == 0)
        {
            FinishPrune(null);
        }
    }

    private void OnPruneReady(ulong senderId, GrPruneReadyMessage message)
    {
        if (_prune == null || _prune.RequestId != message.PruneId || _prune.Phase != PrunePhase.WaitReady)
        {
            return;
        }

        _prune.PendingReady.Remove(senderId);
        _prune.Ready[senderId] = message;

        if (!message.Ok)
        {
            AbortPrune($"玩家 {senderId} 拒绝剪枝：{message.Reason}");
            return;
        }

        RewindRuntime.Instance.LogMpEvent("mp_prune_ready",
            $"req={message.PruneId} peer={senderId} current={message.CurrentId} "
            + $"nodes={message.NodeCount} hash={message.TreeHash}",
            _prune.TargetId);

        if (_prune.PendingReady.Count == 0)
        {
            CommitPrune();
        }
    }

    private void OnPruneCommit(ulong senderId, GrPruneCommitMessage message)
    {
        if (!_net.IsClient || _prune == null || _prune.RequestId != message.PruneId
            || _prune.Phase != PrunePhase.WaitCommit)
        {
            return;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        bool ok = runtime.PruneLocal(message.TargetId, out int removed, out string error);

        _net.SendToHost(new GrPruneDoneMessage
        {
            PruneId = message.PruneId,
            Ok = ok,
            Reason = error ?? string.Empty,
            NodeCount = runtime.Tree.Count,
            TreeHash = runtime.TreeFingerprint()
        });

        if (_lastPrune != null)
        {
            _lastPrune.Pending = false;
            _lastPrune.Ok = ok;
            _lastPrune.Error = error;
            _lastPrune.Removed = removed;
            _lastPrune.NodeCountAfter = runtime.Tree.Count;
            _lastPrune.FinishedMs = Now;
            _lastPrune.Phase = ok ? "done" : "failed";
        }

        runtime.LogMpEvent(ok ? "mp_prune" : "mp_prune_failed",
            $"req={message.PruneId} target=#{message.TargetId} removed={removed} "
            + $"nodes={runtime.Tree.Count}: {error}",
            message.TargetId);

        _prune = null;
        ReleasePruneGuard();
    }

    private void OnPruneDone(ulong senderId, GrPruneDoneMessage message)
    {
        if (_prune == null || _prune.RequestId != message.PruneId || _prune.Phase != PrunePhase.WaitDone)
        {
            return;
        }

        _prune.PendingDone.Remove(senderId);
        _prune.Done[senderId] = message;

        if (_prune.PendingDone.Count == 0)
        {
            FinishPrune(null);
        }
    }

    private void OnPruneAbort(ulong senderId, GrPruneAbortMessage message)
    {
        if (_prune == null || _prune.RequestId != message.PruneId)
        {
            return;
        }

        if (_lastPrune != null)
        {
            _lastPrune.Pending = false;
            _lastPrune.Ok = false;
            _lastPrune.Error = message.Reason ?? "中止";
            _lastPrune.FinishedMs = Now;
            _lastPrune.Phase = "aborted";
        }

        RewindRuntime.Instance.LogMpEvent("mp_prune_abort_recv",
            $"req={message.PruneId}: {message.Reason}", _prune.TargetId);
        _prune = null;
        ReleasePruneGuard();
    }

    private void FinishPrune(string warning)
    {
        if (_prune == null)
        {
            return;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        int nodeCount = runtime.Tree.Count;
        string fingerprint = runtime.TreeFingerprint();

        string failure = warning;
        foreach (KeyValuePair<ulong, GrPruneDoneMessage> pair in _prune.Done)
        {
            GrPruneDoneMessage ack = pair.Value;
            if (!ack.Ok)
            {
                failure ??= $"{pair.Key}: {ack.Reason}";
            }
            else if (ack.NodeCount != nodeCount
                     || !string.Equals(ack.TreeHash, fingerprint, StringComparison.Ordinal))
            {
                failure ??= $"{pair.Key}: 剪枝后树不一致（nodes {ack.NodeCount}/{nodeCount}）";
            }
        }

        if (_lastPrune != null)
        {
            _lastPrune.Pending = false;
            _lastPrune.Ok = failure == null;
            _lastPrune.Error = failure;
            _lastPrune.NodeCountAfter = nodeCount;
            _lastPrune.FinishedMs = Now;
            _lastPrune.Phase = failure == null ? "done" : "diverged";
        }

        if (failure == null)
        {
            runtime.LogMpEvent("mp_prune",
                $"req={_prune.RequestId} target=#{_prune.TargetId} removed={_lastPrune?.Removed ?? 0} "
                + $"nodes={nodeCount} hash={fingerprint}",
                _prune.TargetId);
            ModLog.Info($"mp prune #{_prune.TargetId} ok (nodes={nodeCount})");
        }
        else
        {
            // 已经落盘，无法自动回滚；把差异写进事件流水并大声告警，便于人工介入。
            runtime.LogMpEvent("mp_prune_diverged",
                $"req={_prune.RequestId} target=#{_prune.TargetId}: {failure}", _prune.TargetId);
            ModLog.Error($"mp prune diverged: {failure}");
        }

        _prune = null;
        ReleasePruneGuard();
    }

    private void AbortPrune(string reason)
    {
        if (_prune == null)
        {
            ReleasePruneGuard();
            return;
        }

        _net.SendToAll(new GrPruneAbortMessage
        {
            PruneId = _prune.RequestId,
            Reason = reason ?? "中止"
        });

        if (_lastPrune != null)
        {
            _lastPrune.Pending = false;
            _lastPrune.Ok = false;
            _lastPrune.Error = reason ?? "中止";
            _lastPrune.FinishedMs = Now;
            _lastPrune.Phase = "aborted";
        }

        RewindRuntime.Instance.LogMpEvent("mp_prune_abort",
            $"req={_prune.RequestId} target=#{_prune.TargetId}: {reason}", _prune.TargetId);
        ModLog.Warn($"mp prune aborted: {reason}");

        _prune = null;
        ReleasePruneGuard();
    }

    private void AbortLocalPrune(string reason)
    {
        if (_prune == null)
        {
            ReleasePruneGuard();
            return;
        }

        if (_lastPrune != null)
        {
            _lastPrune.Pending = false;
            _lastPrune.Ok = false;
            _lastPrune.Error = reason ?? "中止";
            _lastPrune.FinishedMs = Now;
            _lastPrune.Phase = "aborted";
        }

        RewindRuntime.Instance.LogMpEvent("mp_prune_abort_local",
            $"req={_prune.RequestId}: {reason}", _prune.TargetId);
        _prune = null;
        ReleasePruneGuard();
    }

    private void OnPrunePrepare(ulong senderId, GrPrunePrepareMessage message)
    {
        if (!_net.IsClient)
        {
            return;
        }

        if (message.Protocol != RewindNetProtocol.Version)
        {
            SendPruneReady(message.PruneId, false, "协议版本不一致");
            return;
        }

        if (!_active)
        {
            SendPruneReady(message.PruneId, false, "本端不在战斗中");
            return;
        }

        if (_prune != null || _freeze != null || _session != null)
        {
            SendPruneReady(message.PruneId, false, "本端已有协同操作在进行中");
            return;
        }

        // 还有锚点没落地：说明本端树落后于房主，剪枝会让两端对不上。
        if (_pendingAnchors.Count > 0)
        {
            SendPruneReady(message.PruneId, false, "本端还有节点锚点未确认");
            return;
        }

        // 必须在“开始挡输入”之前探静止：BeginPruneGuard 会把 PlayerActionsDisabled 置 true。
        if (!QuiescenceProbe.TryCapture(out _, out string reason))
        {
            SendPruneReady(message.PruneId, false, "非静止点：" + reason);
            return;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        if (!runtime.CanPruneTarget(message.TargetId, out string why))
        {
            SendPruneReady(message.PruneId, false, why);
            return;
        }

        NotePeer(senderId, message.Protocol, null);

        _prune = new PruneSession
        {
            RequestId = message.PruneId,
            TargetId = message.TargetId,
            Phase = PrunePhase.WaitCommit,
            DeadlineMs = Now + PruneTimeoutMs
        };

        _lastPrune = new PruneResult
        {
            Pending = true,
            RequestId = message.PruneId,
            TargetId = message.TargetId,
            NodeCountBefore = runtime.Tree.Count,
            StartedMs = Now,
            Phase = "ready"
        };

        BeginPruneGuard();

        SendPruneReady(message.PruneId, true, null,
            runtime.Tree.CurrentId, runtime.Tree.Count, runtime.TreeFingerprint());

        runtime.LogMpEvent("mp_prune_ready_sent",
            $"req={message.PruneId} target=#{message.TargetId} current={runtime.Tree.CurrentId} "
            + $"nodes={runtime.Tree.Count} hash={runtime.TreeFingerprint()}",
            message.TargetId);
    }

    private void SendPruneReady(int pruneId, bool ok, string reason,
        int currentId = 0, int nodeCount = 0, string treeHash = null)
    {
        _net.SendToHost(new GrPruneReadyMessage
        {
            PruneId = pruneId,
            Ok = ok,
            Reason = reason ?? string.Empty,
            CurrentId = currentId,
            NodeCount = nodeCount,
            TreeHash = treeHash ?? string.Empty
        });
    }

    // ---------------------------------------------------------------- 本端冻结 / 恢复

    private void BeginLocalFreeze(int nodeId, int requestId, uint expectedChecksum, int idleTimeoutMs)
    {
        _freeze = new LocalFreeze
        {
            RequestId = requestId,
            NodeId = nodeId,
            ExpectedChecksum = expectedChecksum,
            Phase = JumpPhase.Freezing,
            IdleDeadlineMs = Now + (ulong)idleTimeoutMs,
            PhaseDeadlineMs = Now + (ulong)idleTimeoutMs
        };

        RewindRuntime.Instance.SetCaptureSuspended(true);
        SetInputBlocked(true);
    }

    /// <summary>冻结后等本机真正静止；静止时抓一份安全快照，供失败回滚。</summary>
    private bool TryReachIdle()
    {
        if (_freeze == null)
        {
            return false;
        }

        if (_freeze.Idle)
        {
            return true;
        }

        if (!QuiescenceProbe.TryCapture(out Frontier frontier, out string reason,
                ignoreLocalInputBlock: true))
        {
            _freeze.LastReason = reason;
            return false;
        }

        if (!CombatChecksum.ComputePure(out uint checksum))
        {
            _freeze.LastReason = "无法计算本端校验和";
            return false;
        }

        ObjectGraphSnapshot safety = RewindRuntime.Instance.CaptureSafetySnapshot();
        if (safety == null)
        {
            _freeze.LastReason = "安全快照失败";
            return false;
        }

        _freeze.Safety = safety;
        _freeze.LocalChecksum = checksum;
        _freeze.Idle = true;
        _freeze.Phase = _net.IsClient ? JumpPhase.WaitCommit : JumpPhase.Idle;

        // 队列在静止点才暂停：提前暂停会让在途动作永远等不到执行，探针反而永远不静止。
        PauseQueues();

        if (_net.IsClient)
        {
            _net.SendToHost(new GrIdleMessage
            {
                RequestId = _freeze.RequestId,
                Ok = true,
                Checksum = checksum,
                Frontier = frontier
            });
        }

        RewindRuntime.Instance.LogMpEvent("mp_idle",
            $"req={_freeze.RequestId} checksum={checksum:x8} frontier={frontier?.Describe()}",
            _freeze.NodeId);
        return true;
    }

    private void PumpLocalFreeze()
    {
        if (_freeze == null)
        {
            return;
        }

        ulong now = Now;

        if (_freeze.Phase == JumpPhase.Freezing)
        {
            if (TryReachIdle())
            {
                _freeze.PhaseDeadlineMs = now + JumpRestoreTimeoutMs;
            }
            else if (now >= _freeze.IdleDeadlineMs)
            {
                string reason = "本端未能到达静止点：" + (_freeze.LastReason ?? "unknown");
                if (_net.IsClient)
                {
                    _net.SendToHost(new GrIdleMessage
                    {
                        RequestId = _freeze.RequestId,
                        Ok = false,
                        Reason = reason
                    });
                    _freeze.Phase = JumpPhase.WaitAbort;
                    _freeze.PhaseDeadlineMs = now + JumpRestoreTimeoutMs;
                    RewindRuntime.Instance.LogMpEvent("mp_idle_failed", reason, _freeze.NodeId);
                }
                else
                {
                    AbortHostSession(reason);
                }
            }

            return;
        }

        if (_freeze.Phase == JumpPhase.WaitCommit && now >= _freeze.PhaseDeadlineMs)
        {
            AbortLocal("等待房主提交超时");
            return;
        }

        if ((_freeze.Phase == JumpPhase.WaitResume || _freeze.Phase == JumpPhase.WaitAbort)
            && now >= _freeze.PhaseDeadlineMs)
        {
            AbortLocal(_freeze.Phase == JumpPhase.WaitResume ? "等待房主收尾超时" : "等待房主中止超时");
        }
    }

    private void PumpHostSession()
    {
        if (_session == null)
        {
            return;
        }

        ulong now = Now;

        if (_session.Phase == JumpPhase.Idle)
        {
            if (_freeze == null || !_freeze.Idle)
            {
                return;
            }

            if (_session.PendingIdle.Count == 0)
            {
                CommitAsHost();
                return;
            }

            if (now >= _session.DeadlineMs)
            {
                AbortHostSession("等待其他玩家静止超时");
            }

            return;
        }

        if (_session.Phase == JumpPhase.WaitRestored)
        {
            if (_session.PendingRestored.Count == 0)
            {
                FinishHostSession();
                return;
            }

            if (now >= _session.DeadlineMs)
            {
                AbortHostSession("等待其他玩家恢复确认超时");
            }
        }
    }

    private void CommitAsHost()
    {
        if (_session == null || _freeze == null)
        {
            return;
        }

        // 冻结后仍然可能有动作挤进队列（例如按钮在禁用生效前被点了一下）：
        // 这种时候写回会把那个动作永远留在队列里，宁可直接中止。
        if (!IsActionQueueEmpty())
        {
            AbortHostSession("冻结期间有新的动作入队");
            return;
        }

        if (_session.IdleChecksum != 0 && _freeze.LocalChecksum != _session.IdleChecksum)
        {
            AbortHostSession(
                $"房主状态与当前节点不一致（{_freeze.LocalChecksum:x8} != {_session.IdleChecksum:x8}）");
            return;
        }

        RestoreOutcome outcome = RewindRuntime.Instance.ApplyMpRestore(_session.NodeId, _freeze.Safety);
        // 安全快照要留到 Resume/Abort 定论之后再丢：Commit 成功不代表整场跳转成功，
        // 只要有一个对端失败，本端也要靠它退回去（M2 实测踩过：提前置空导致房主回滚不了）。
        _freeze.Restored = outcome.Ok;

        if (!outcome.Ok)
        {
            AbortHostSession("房主写回失败：" + (outcome.Error ?? "unknown"));
            return;
        }

        _session.Phase = JumpPhase.WaitRestored;
        _session.DeadlineMs = Now + JumpRestoreTimeoutMs;
        foreach (ulong peer in _net.Peers())
        {
            _session.PendingRestored.Add(peer);
        }

        _net.SendToAll(new GrCommitMessage
        {
            RequestId = _session.RequestId,
            NodeId = _session.NodeId,
            HostChecksum = outcome.Checksum
        });

        RewindRuntime.Instance.LogMpEvent("mp_commit",
            $"req={_session.RequestId} node=#{_session.NodeId} checksum={outcome.Checksum:x8} "
            + $"mismatches={outcome.Mismatches} deep_equal={outcome.DeepEqual}",
            _session.NodeId);

        if (_session.PendingRestored.Count == 0)
        {
            FinishHostSession();
        }
    }

    private void FinishHostSession()
    {
        if (_session == null)
        {
            return;
        }

        string failure = null;
        foreach (KeyValuePair<ulong, GrRestoredMessage> pair in _session.Restored)
        {
            GrRestoredMessage ack = pair.Value;
            if (!ack.Ok)
            {
                failure ??= $"{pair.Key}: {ack.Error}";
            }
            else if (ack.Checksum != _session.ExpectedChecksum)
            {
                failure ??= $"{pair.Key}: checksum {ack.Checksum:x8} != {_session.ExpectedChecksum:x8}";
            }
        }

        if (failure != null)
        {
            AbortHostSession("其他玩家恢复不一致：" + failure);
            return;
        }

        RewindRuntime.Instance.CommitRestoredNode(_session.NodeId);
        _net.SendToAll(new GrResumeMessage
        {
            RequestId = _session.RequestId,
            NodeId = _session.NodeId
        });

        RewindRuntime.Instance.LogMpEvent("mp_resume",
            $"req={_session.RequestId} node=#{_session.NodeId} 全员一致", _session.NodeId);
        ModLog.Info($"mp jump to #{_session.NodeId} ok (req {_session.RequestId})");

        if (_lastResult != null)
        {
            _lastResult.Pending = false;
            _lastResult.Ok = true;
            _lastResult.Error = null;
            _lastResult.FinishedMs = Now;
            _lastResult.Phase = "resumed";
        }

        _session = null;
        EndFreeze();
    }

    private void AbortHostSession(string reason)
    {
        if (_session == null)
        {
            AbortLocal(reason);
            return;
        }

        bool rollback = _freeze != null && _freeze.Restored;
        _net.SendToAll(new GrAbortMessage
        {
            RequestId = _session.RequestId,
            Reason = reason ?? "中止",
            Rollback = rollback
        });

        RewindRuntime.Instance.LogMpEvent("mp_abort",
            $"req={_session.RequestId} node=#{_session.NodeId} rollback={rollback}: {reason}",
            _session.NodeId);
        ModLog.Warn($"mp jump aborted: {reason}");

        if (rollback)
        {
            RollbackLocal();
        }

        if (_lastResult != null)
        {
            _lastResult.Pending = false;
            _lastResult.Ok = false;
            _lastResult.Error = reason ?? "中止";
            _lastResult.FinishedMs = Now;
            _lastResult.Phase = "aborted";
        }

        _session = null;
        EndFreeze();
    }

    private void AbortLocal(string reason)
    {
        if (_freeze == null)
        {
            return;
        }

        bool restored = _freeze.Restored;
        if (restored)
        {
            RollbackLocal();
        }

        RewindRuntime.Instance.LogMpEvent("mp_abort_local",
            $"req={_freeze.RequestId} rollback={restored}: {reason}", _freeze.NodeId);
        ModLog.Warn($"mp local abort: {reason}");

        if (_lastResult != null)
        {
            _lastResult.Pending = false;
            _lastResult.Ok = false;
            _lastResult.Error = reason ?? "中止";
            _lastResult.FinishedMs = Now;
            _lastResult.Phase = "aborted";
        }

        EndFreeze();
    }

    private void RollbackLocal()
    {
        ObjectGraphSnapshot safety = _freeze?.Safety;
        _freeze.Safety = null;
        _freeze.Restored = false;
        if (safety == null)
        {
            return;
        }

        try
        {
            safety.Restore();
            if (RewindRuntime.Instance.IsCombatActive)
            {
                RewindRuntime.Instance.RebuildAfterRestore();
            }

            RewindRuntime.Instance.LogMpEvent("mp_rollback", "已退回冻结时的安全快照", _freeze?.NodeId ?? 0);
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp rollback failed: {e.GetType().Name} {e.Message}");
        }
    }

    private void EndFreeze()
    {
        _freeze = null;
        RewindRuntime.Instance.SetCaptureSuspended(false);
        UnpauseQueues();
        SetInputBlocked(false);
        RewindRuntime.Instance.NotifyChangedPublic();
        RewindRuntime.Instance.PumpDeferredCapture();
    }

    // ---------------------------------------------------------------- 消息分发

    private void Enqueue(INetMessage message, ulong senderId)
    {
        if (message == null)
        {
            return;
        }

        // 锚点必须在“下一条动作被执行之前”落地：
        // 网络回调运行在主线程的 NetService.Update 里，早于本帧的 ProcessFrame；
        // 在这里同步处理，才能拿到锚点所指的那一个状态（否则客户端可能已经把
        // 紧随其后的动作执行了一半，checksum 就对不上了）。
        // 冻结同理：早一帧挡住输入，就少一次“冻结期间点了牌”的意外。
        if (_net.IsClient && (message is GrAnchorMessage || message is GrFreezeMessage))
        {
            try
            {
                Dispatch(message, senderId);
            }
            catch (Exception e)
            {
                ModLog.Warn($"mp immediate {message.GetType().Name} failed: {e.GetType().Name} {e.Message}");
            }

            return;
        }

        _inbox.Enqueue(new Incoming(message, senderId));
    }

    private void DrainInbox()
    {
        int budget = 64;
        while (budget-- > 0 && _inbox.TryDequeue(out Incoming incoming))
        {
            try
            {
                Dispatch(incoming.Message, incoming.SenderId);
            }
            catch (Exception e)
            {
                ModLog.Warn($"mp dispatch {incoming.Message.GetType().Name} failed: "
                            + $"{e.GetType().Name} {e.Message}");
            }
        }
    }

    private void Dispatch(INetMessage message, ulong senderId)
    {
        switch (message)
        {
            case GrHelloMessage hello:
                OnHello(senderId, hello);
                break;
            case GrHelloAckMessage ack:
                OnHelloAck(senderId, ack);
                break;
            case GrAnchorMessage anchor:
                OnAnchor(senderId, anchor);
                break;
            case GrAnchorAckMessage ack:
                OnAnchorAck(senderId, ack);
                break;
            case GrFreezeMessage freeze:
                OnFreeze(senderId, freeze);
                break;
            case GrIdleMessage idle:
                OnIdle(senderId, idle);
                break;
            case GrCommitMessage commit:
                OnCommit(senderId, commit);
                break;
            case GrRestoredMessage restored:
                OnRestored(senderId, restored);
                break;
            case GrResumeMessage resume:
                OnResume(senderId, resume);
                break;
            case GrAbortMessage abort:
                OnAbort(senderId, abort);
                break;
            case GrJumpRequestMessage request:
                OnJumpRequest(senderId, request);
                break;
            case GrCurrentMessage current:
                OnCurrent(senderId, current);
                break;
            case GrPrunePrepareMessage prepare:
                OnPrunePrepare(senderId, prepare);
                break;
            case GrPruneReadyMessage ready:
                OnPruneReady(senderId, ready);
                break;
            case GrPruneCommitMessage commit:
                OnPruneCommit(senderId, commit);
                break;
            case GrPruneDoneMessage done:
                OnPruneDone(senderId, done);
                break;
            case GrPruneAbortMessage pruneAbort:
                OnPruneAbort(senderId, pruneAbort);
                break;
        }
    }

    private void OnHello(ulong senderId, GrHelloMessage message)
    {
        if (_net.IsHost)
        {
            return;
        }

        NotePeer(senderId, message.Protocol, message.ModVersion);
        _combatIndex = message.CombatIndex;

        CombatChecksum.ComputePure(out uint checksum);
        _net.SendToHost(new GrHelloAckMessage
        {
            Protocol = RewindNetProtocol.Version,
            ModVersion = RewindNetProtocol.ModVersion,
            Checksum = checksum
        });
    }

    private void OnHelloAck(ulong senderId, GrHelloAckMessage message)
    {
        NotePeer(senderId, message.Protocol, message.ModVersion);

        if (message.Protocol != RewindNetProtocol.Version)
        {
            _lastHelloError = $"对端 {senderId} 协议版本 {message.Protocol}，本端 {RewindNetProtocol.Version}";
            ModLog.Warn(_lastHelloError);
            return;
        }

        if (!string.Equals(message.ModVersion, RewindNetProtocol.ModVersion, StringComparison.Ordinal))
        {
            _lastHelloError = $"对端 {senderId} mod 版本 {message.ModVersion}，本端 {RewindNetProtocol.ModVersion}";
            ModLog.Warn(_lastHelloError);
        }

        RewindRuntime.Instance.LogMpEvent("mp_hello",
            $"peer {senderId} protocol={message.Protocol} version={message.ModVersion} checksum={message.Checksum:x8}",
            0, senderId);
    }

    private void NotePeer(ulong peerId, int protocol, string version)
    {
        _peers[peerId] = new PeerState
        {
            PeerId = peerId,
            Protocol = protocol,
            ModVersion = version ?? string.Empty,
            LastSeenMs = Now
        };
    }

    private bool CheckPeersProtocol(out string error)
    {
        error = null;
        foreach (ulong peer in _net.Peers())
        {
            if (!_peers.TryGetValue(peer, out PeerState state))
            {
                // 还没握手并不致命：锚点/冻结消息本身也带协议版本，对端会在处理时校验。
                continue;
            }

            if (state.Protocol != RewindNetProtocol.Version)
            {
                error = $"对端 {peer} 协议版本不一致（{state.Protocol} != {RewindNetProtocol.Version}）";
                return false;
            }
        }

        return true;
    }

    private void OnAnchor(ulong senderId, GrAnchorMessage message)
    {
        if (!_net.IsClient)
        {
            return;
        }

        if (message.Protocol != RewindNetProtocol.Version)
        {
            SendAnchorAck(message.NodeId, false, 0,
                $"协议版本不一致（房主 {message.Protocol} / 本端 {RewindNetProtocol.Version}）");
            return;
        }

        NotePeer(senderId, message.Protocol, null);

        RewindNode existing = RewindRuntime.Instance.Tree.Get(message.NodeId);
        if (existing != null)
        {
            AckExistingMirror(existing);
            return;
        }

        if (message.Force && TryBindAnchor(message, out _))
        {
            return;
        }

        // 房主的根节点锚点通常比本端的 CombatBegan 早到（本端还在进战斗房间）。
        // 这时挂起、等 Pump 在战斗就绪后重试；早期版本在这里被 OnCombatBegan 清掉，
        // 结果根节点缺失、后续锚点全部建点失败，客户端树一直是空的。
        if (!_active)
        {
            RewindRuntime.Instance.LogMpEvent("mp_anchor_early",
                $"node #{message.NodeId} 早于本端 CombatBegan 到达，先挂起等战斗就绪",
                message.NodeId);
        }

        _anchorParents[message.NodeId] = message.ParentId;
        _pendingAnchors.Add(new PendingAnchor
        {
            Message = message,
            DeadlineMs = Now + AnchorTimeoutMs
        });
    }

    private void OnFreeze(ulong senderId, GrFreezeMessage message)
    {
        if (!_net.IsClient)
        {
            return;
        }

        if (message.Protocol != RewindNetProtocol.Version)
        {
            _net.SendToHost(new GrIdleMessage
            {
                RequestId = message.RequestId,
                Ok = false,
                Reason = "协议版本不一致"
            });
            return;
        }

        if (_freeze != null || _session != null)
        {
            _net.SendToHost(new GrIdleMessage
            {
                RequestId = message.RequestId,
                Ok = false,
                Reason = "本端已有跳转在进行中"
            });
            return;
        }

        if (!_active)
        {
            _net.SendToHost(new GrIdleMessage
            {
                RequestId = message.RequestId,
                Ok = false,
                Reason = "本端不在战斗中"
            });
            return;
        }

        NotePeer(senderId, message.Protocol, null);
        BeginLocalFreeze(message.NodeId, message.RequestId, message.ExpectedChecksum,
            message.TimeoutMs > 0 ? message.TimeoutMs : JumpIdleTimeoutMs);

        // 跟着跳的客户端也要有可查询的结果（rew_jump_status 两端一致）。
        if (_lastResult == null || _lastResult.RequestId != message.RequestId)
        {
            _lastResult = new JumpResult
            {
                Pending = true,
                RequestId = message.RequestId,
                NodeId = message.NodeId,
                ExpectedChecksum = message.ExpectedChecksum,
                StartedMs = Now,
                Phase = "frozen"
            };
        }

        RewindRuntime.Instance.LogMpEvent("mp_freeze_recv",
            $"req={message.RequestId} target=#{message.NodeId}", message.NodeId);
    }

    private void OnIdle(ulong senderId, GrIdleMessage message)
    {
        if (_session == null || _session.RequestId != message.RequestId)
        {
            return;
        }

        _session.PendingIdle.Remove(senderId);
        _session.Idles[senderId] = message;

        if (!message.Ok)
        {
            AbortHostSession($"玩家 {senderId} 未静止：{message.Reason}");
            return;
        }

        if (_session.IdleChecksum != 0 && message.Checksum != _session.IdleChecksum)
        {
            AbortHostSession(
                $"玩家 {senderId} 状态与当前节点不一致（{message.Checksum:x8} != {_session.IdleChecksum:x8}）");
            return;
        }

        if (_session.IdleFrontier != null && message.Frontier != null
            && !_session.IdleFrontier.SameAs(message.Frontier))
        {
            AbortHostSession($"玩家 {senderId} 切面与当前节点不一致");
            return;
        }

        RewindRuntime.Instance.LogMpEvent("mp_idle_recv",
            $"req={message.RequestId} peer={senderId} checksum={message.Checksum:x8}", _session.NodeId);
    }

    private void OnCommit(ulong senderId, GrCommitMessage message)
    {
        if (!_net.IsClient || _freeze == null || _freeze.RequestId != message.RequestId)
        {
            return;
        }

        if (_freeze.Phase == JumpPhase.WaitAbort)
        {
            return;
        }

        if (_freeze.Phase != JumpPhase.Idle && _freeze.Phase != JumpPhase.WaitCommit)
        {
            return;
        }

        if (!IsActionQueueEmpty())
        {
            _freeze.Phase = JumpPhase.WaitAbort;
            _freeze.PhaseDeadlineMs = Now + JumpRestoreTimeoutMs;
            _net.SendToHost(new GrRestoredMessage
            {
                RequestId = message.RequestId,
                Ok = false,
                Checksum = 0,
                Mismatches = -1,
                Error = "冻结期间有新的动作入队"
            });
            return;
        }

        RestoreOutcome outcome = RewindRuntime.Instance.ApplyMpRestore(message.NodeId, _freeze.Safety);
        _freeze.Restored = outcome.Ok;
        _freeze.Phase = JumpPhase.WaitResume;
        _freeze.PhaseDeadlineMs = Now + JumpRestoreTimeoutMs;

        _net.SendToHost(new GrRestoredMessage
        {
            RequestId = message.RequestId,
            Ok = outcome.Ok,
            Checksum = outcome.Checksum,
            Mismatches = outcome.Mismatches,
            Error = outcome.Error ?? string.Empty
        });

        RewindRuntime.Instance.LogMpEvent("mp_restored",
            $"req={message.RequestId} node=#{message.NodeId} ok={outcome.Ok} "
            + $"checksum={outcome.Checksum:x8} mismatches={outcome.Mismatches}",
            message.NodeId);

        if (!outcome.Ok)
        {
            // 本端已经自行回滚；等房主的 Abort 收尾，避免在别人还冻结时先解冻。
            _freeze.Phase = JumpPhase.WaitAbort;
        }
    }

    private void OnRestored(ulong senderId, GrRestoredMessage message)
    {
        if (_session == null || _session.RequestId != message.RequestId)
        {
            return;
        }

        _session.PendingRestored.Remove(senderId);
        _session.Restored[senderId] = message;

        if (!message.Ok || message.Checksum != _session.ExpectedChecksum)
        {
            FinishHostSession();
        }
    }

    private void OnResume(ulong senderId, GrResumeMessage message)
    {
        if (!_net.IsClient || _freeze == null || _freeze.RequestId != message.RequestId)
        {
            return;
        }

        RewindRuntime.Instance.CommitRestoredNode(message.NodeId);

        if (_lastResult != null)
        {
            _lastResult.Pending = false;
            _lastResult.Ok = true;
            _lastResult.Error = null;
            _lastResult.FinishedMs = Now;
            _lastResult.Phase = "resumed";
        }

        RewindRuntime.Instance.LogMpEvent("mp_resume_recv",
            $"req={message.RequestId} node=#{message.NodeId}", message.NodeId);
        EndFreeze();
    }

    private void OnAbort(ulong senderId, GrAbortMessage message)
    {
        if (_freeze == null || _freeze.RequestId != message.RequestId)
        {
            // 房主拒绝客户端的跳转请求时也走 Abort，但此时本端还没有 freeze。
            if (_lastResult != null && _lastResult.RequestId == message.RequestId)
            {
                _lastResult.Pending = false;
                _lastResult.Ok = false;
                _lastResult.Error = message.Reason;
                _lastResult.FinishedMs = Now;
                _lastResult.Phase = "rejected";
            }

            return;
        }

        if (message.Rollback && _freeze.Restored)
        {
            RollbackLocal();
        }

        if (_lastResult != null)
        {
            _lastResult.Pending = false;
            _lastResult.Ok = false;
            _lastResult.Error = message.Reason ?? "中止";
            _lastResult.FinishedMs = Now;
            _lastResult.Phase = "aborted";
        }

        RewindRuntime.Instance.LogMpEvent("mp_abort_recv",
            $"req={message.RequestId} rollback={message.Rollback}: {message.Reason}", _freeze.NodeId);
        EndFreeze();
    }

    private void OnJumpRequest(ulong senderId, GrJumpRequestMessage message)
    {
        if (!_net.IsHost || !_active)
        {
            return;
        }

        RewindNode node = RewindRuntime.Instance.Tree.Get(message.NodeId);
        if (node == null || InSession)
        {
            _net.SendToPeer(new GrAbortMessage
            {
                RequestId = message.RequestId,
                Reason = node == null ? $"节点 #{message.NodeId} 不存在" : "房主已有跳转在进行中",
                Rollback = false
            }, senderId);
            return;
        }

        if (!StartHostSession(node, message.RequestId, senderId, out string error))
        {
            _net.SendToPeer(new GrAbortMessage
            {
                RequestId = message.RequestId,
                Reason = error ?? "房主拒绝跳转",
                Rollback = false
            }, senderId);
            return;
        }

        RewindRuntime.Instance.LogMpEvent("mp_jump_request_recv",
            $"peer={senderId} req={message.RequestId} node=#{message.NodeId}", message.NodeId);
    }

    /// <summary>房主把“当前节点”同步给客户端（重放同一动作合并到旧节点时用）。</summary>
    public void BroadcastCurrent(int nodeId)
    {
        if (!_active || !_net.IsHost || nodeId <= 0)
        {
            return;
        }

        _net.SendToAll(new GrCurrentMessage
        {
            Protocol = RewindNetProtocol.Version,
            NodeId = nodeId
        });
    }

    private void OnCurrent(ulong senderId, GrCurrentMessage message)
    {
        if (!_net.IsClient || message.Protocol != RewindNetProtocol.Version)
        {
            return;
        }

        RewindRuntime runtime = RewindRuntime.Instance;
        RewindNode node = runtime.Tree.Get(message.NodeId);
        if (node == null || runtime.Tree.CurrentId == message.NodeId)
        {
            return;
        }

        runtime.SetCurrentNode(message.NodeId);
        runtime.LogMpEvent("mp_current", $"房主把当前节点推进到 #{message.NodeId}", message.NodeId);
    }

    // ---------------------------------------------------------------- 输入与队列

    /// <summary>
    /// 写回成功之后重新压住本地输入。快照里的 `_playerActionsDisabled` 是“写回那一刻”的真值
    /// （通常是 false），但协同跳转在 Resume 之前仍然必须保持冻结。
    /// </summary>
    internal void KeepFrozen()
    {
        if (_freeze == null)
        {
            return;
        }

        SetInputBlocked(true);
        PauseQueues();
    }

    private static FieldInfo _actionsDisabledField;
    private static FieldInfo _actionsDisabledEventField;

    /// <summary>
    /// 冻结期间挡住本地输入：`_playerActionsDisabled` 是手牌/药水/结束回合共同的权威开关，
    /// 但它只有私有 setter，且 UI 依赖 `PlayerActionsDisabledChanged` 事件刷新，所以两者都用反射。
    /// </summary>
    private static void SetInputBlocked(bool blocked)
    {
        try
        {
            CombatManager manager = CombatManager.Instance;
            if (manager == null)
            {
                return;
            }

            _actionsDisabledField ??= typeof(CombatManager).GetField(
                "_playerActionsDisabled", BindingFlags.Instance | BindingFlags.NonPublic);

            bool current = manager.PlayerActionsDisabled;
            _actionsDisabledField?.SetValue(manager, blocked);

            if (current == blocked)
            {
                return;
            }

            _actionsDisabledEventField ??= typeof(CombatManager).GetField(
                "PlayerActionsDisabledChanged", BindingFlags.Instance | BindingFlags.NonPublic);
            CombatState combat = PlayerScope.CombatState;
            if (combat != null && _actionsDisabledEventField?.GetValue(manager) is Action<CombatState> handler)
            {
                handler.Invoke(combat);
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp input block failed: {e.Message}");
        }
    }

    private static void PauseQueues()
    {
        try
        {
            RunManager.Instance?.ActionQueueSet?.PauseAllPlayerQueues();
            RunManager.Instance?.ActionExecutor?.Pause();
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp pause queues failed: {e.Message}");
        }
    }

    private static void UnpauseQueues()
    {
        try
        {
            RunManager.Instance?.ActionQueueSet?.UnpauseAllPlayerQueues();
            RunManager.Instance?.ActionExecutor?.Unpause();
        }
        catch (Exception e)
        {
            ModLog.Warn($"mp unpause queues failed: {e.Message}");
        }
    }

    private static bool IsActionQueueEmpty()
    {
        try
        {
            return RunManager.Instance?.ActionQueueSet?.IsEmpty ?? true;
        }
        catch
        {
            return true;
        }
    }

    // ---------------------------------------------------------------- 状态输出

    public Dictionary<string, object> Status()
    {
        var peers = new List<object>(4);
        foreach (PeerState state in _peers.Values.OrderBy(p => p.PeerId))
        {
            peers.Add(new Dictionary<string, object>
            {
                ["net_id"] = state.PeerId,
                ["protocol"] = state.Protocol,
                ["protocol_ok"] = state.Protocol == RewindNetProtocol.Version,
                ["mod_version"] = state.ModVersion,
                ["last_seen_ms"] = state.LastSeenMs
            });
        }

        var pendingNodes = new List<object>(_pendingNodes.Count);
        foreach (PendingNode pending in _pendingNodes.Values)
        {
            pendingNodes.Add(new Dictionary<string, object>
            {
                ["node_id"] = pending.NodeId,
                ["waiting"] = pending.Waiting.ToArray(),
                ["acks"] = pending.Acks.Count,
                ["timeout"] = pending.Timeout
            });
        }

        return new Dictionary<string, object>
        {
            ["active"] = _active,
            ["role"] = _net.IsHost ? "host" : _net.IsClient ? "client" : "none",
            ["protocol"] = RewindNetProtocol.Version,
            ["mod_version"] = RewindNetProtocol.ModVersion,
            ["local_net_id"] = PlayerScope.LocalNetId,
            ["connected"] = _net.IsConnected,
            ["peer_count"] = _net.Peers().Count,
            ["peers"] = peers,
            ["last_hello_error"] = _lastHelloError,
            ["in_session"] = InSession,
            ["frozen"] = _freeze != null,
            ["session_phase"] = CurrentPhase(),
            ["session"] = _session?.ToDictionary(),
            ["freeze"] = _freeze?.ToDictionary(),
            ["pending_anchors"] = _pendingAnchors.Count,
            ["pending_nodes"] = pendingNodes,
            ["mirror_cache"] = _mirrorCache.Count,
            ["prune"] = _prune?.ToDictionary(),
            ["prune_guard"] = _pruneGuard,
            ["last_prune"] = _lastPrune?.ToDictionary(),
            ["last_result"] = _lastResult?.ToDictionary()
        };
    }

    private string CurrentPhase()
    {
        if (_session != null)
        {
            return _session.Phase.ToString();
        }

        if (_freeze != null)
        {
            return _freeze.Phase.ToString();
        }

        if (_prune != null)
        {
            return "prune:" + _prune.Phase;
        }

        return "Idle";
    }

    private static ulong Now => Time.GetTicksMsec();

    // ---------------------------------------------------------------- 内部类型

    private readonly struct Incoming
    {
        public readonly INetMessage Message;
        public readonly ulong SenderId;

        public Incoming(INetMessage message, ulong senderId)
        {
            Message = message;
            SenderId = senderId;
        }
    }

    private sealed class PeerState
    {
        public ulong PeerId;
        public int Protocol;
        public string ModVersion;
        public ulong LastSeenMs;
    }

    private sealed class PendingAnchor
    {
        public GrAnchorMessage Message;
        public ulong DeadlineMs;
        public string LastReason;
    }

    private sealed class PendingNode
    {
        public int NodeId;
        public uint Checksum;
        public ulong DeadlineMs;
        public bool Timeout;
        public readonly HashSet<ulong> Waiting = new HashSet<ulong>();
        public readonly Dictionary<ulong, GrAnchorAckMessage> Acks =
            new Dictionary<ulong, GrAnchorAckMessage>();
    }

    private sealed class LocalFreeze
    {
        public int RequestId;
        public int NodeId;
        public uint ExpectedChecksum;
        public uint LocalChecksum;
        public JumpPhase Phase;
        public ulong IdleDeadlineMs;
        public ulong PhaseDeadlineMs;
        public ObjectGraphSnapshot Safety;
        public bool Idle;
        public bool Restored;
        public string LastReason;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["request_id"] = RequestId,
                ["node_id"] = NodeId,
                ["expected_checksum"] = ExpectedChecksum.ToString("x8"),
                ["local_checksum"] = LocalChecksum.ToString("x8"),
                ["phase"] = Phase.ToString(),
                ["idle"] = Idle,
                ["restored"] = Restored,
                ["has_safety"] = Safety != null,
                ["last_reason"] = LastReason
            };
        }
    }

    private sealed class JumpSession
    {
        public int RequestId;
        public int NodeId;
        public uint ExpectedChecksum;
        public Frontier ExpectedFrontier;
        public uint IdleChecksum;
        public Frontier IdleFrontier;
        public ulong Requester;
        public JumpPhase Phase;
        public ulong DeadlineMs;
        public readonly HashSet<ulong> PendingIdle = new HashSet<ulong>();
        public readonly Dictionary<ulong, GrIdleMessage> Idles =
            new Dictionary<ulong, GrIdleMessage>();
        public readonly HashSet<ulong> PendingRestored = new HashSet<ulong>();
        public readonly Dictionary<ulong, GrRestoredMessage> Restored =
            new Dictionary<ulong, GrRestoredMessage>();

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["request_id"] = RequestId,
                ["node_id"] = NodeId,
                ["expected_checksum"] = ExpectedChecksum.ToString("x8"),
                ["requester"] = Requester,
                ["phase"] = Phase.ToString(),
                ["pending_idle"] = PendingIdle.ToArray(),
                ["idle_acks"] = Idles.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => (object)new Dictionary<string, object>
                    {
                        ["ok"] = pair.Value.Ok,
                        ["checksum"] = pair.Value.Checksum.ToString("x8"),
                        ["reason"] = pair.Value.Reason
                    }),
                ["pending_restored"] = PendingRestored.ToArray(),
                ["restored_acks"] = Restored.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => (object)new Dictionary<string, object>
                    {
                        ["ok"] = pair.Value.Ok,
                        ["checksum"] = pair.Value.Checksum.ToString("x8"),
                        ["mismatches"] = pair.Value.Mismatches,
                        ["error"] = pair.Value.Error
                    })
            };
        }
    }

    private sealed class CachedMirror
    {
        public uint Checksum;
        public Frontier Frontier;
        public ObjectGraphSnapshot Snapshot;
        public ulong CreatedMs;
    }

    private sealed class PruneSession
    {
        public int RequestId;
        public int TargetId;
        public PrunePhase Phase;
        public ulong DeadlineMs;
        public readonly HashSet<ulong> PendingReady = new HashSet<ulong>();
        public readonly Dictionary<ulong, GrPruneReadyMessage> Ready =
            new Dictionary<ulong, GrPruneReadyMessage>();
        public readonly HashSet<ulong> PendingDone = new HashSet<ulong>();
        public readonly Dictionary<ulong, GrPruneDoneMessage> Done =
            new Dictionary<ulong, GrPruneDoneMessage>();

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["request_id"] = RequestId,
                ["target_id"] = TargetId,
                ["phase"] = Phase.ToString(),
                ["pending_ready"] = PendingReady.ToArray(),
                ["ready"] = Ready.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => (object)new Dictionary<string, object>
                    {
                        ["ok"] = pair.Value.Ok,
                        ["reason"] = pair.Value.Reason,
                        ["current_id"] = pair.Value.CurrentId,
                        ["node_count"] = pair.Value.NodeCount,
                        ["tree_hash"] = pair.Value.TreeHash
                    }),
                ["pending_done"] = PendingDone.ToArray(),
                ["done"] = Done.ToDictionary(
                    pair => pair.Key.ToString(),
                    pair => (object)new Dictionary<string, object>
                    {
                        ["ok"] = pair.Value.Ok,
                        ["reason"] = pair.Value.Reason,
                        ["node_count"] = pair.Value.NodeCount,
                        ["tree_hash"] = pair.Value.TreeHash
                    })
            };
        }
    }

    internal sealed class PruneResult
    {
        public bool Pending;
        public bool Ok;
        public int RequestId;
        public int TargetId;
        public int Removed;
        public int NodeCountBefore;
        public int NodeCountAfter;
        public ulong StartedMs;
        public ulong FinishedMs;
        public string Phase;
        public string Error;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["pending"] = Pending,
                ["ok"] = Ok,
                ["request_id"] = RequestId,
                ["target_id"] = TargetId,
                ["removed"] = Removed,
                ["node_count_before"] = NodeCountBefore,
                ["node_count_after"] = NodeCountAfter,
                ["phase"] = Phase,
                ["error"] = Error,
                ["elapsed_ms"] = FinishedMs > StartedMs ? FinishedMs - StartedMs : Now - StartedMs
            };
        }
    }

    internal sealed class JumpResult
    {
        public bool Pending;
        public bool Ok;
        public int RequestId;
        public int NodeId;
        public uint ExpectedChecksum;
        public ulong StartedMs;
        public ulong FinishedMs;
        public string Phase;
        public string Error;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                ["pending"] = Pending,
                ["ok"] = Ok,
                ["request_id"] = RequestId,
                ["node_id"] = NodeId,
                ["expected_checksum"] = ExpectedChecksum.ToString("x8"),
                ["phase"] = Phase,
                ["error"] = Error,
                ["elapsed_ms"] = FinishedMs > StartedMs ? FinishedMs - StartedMs : Now - StartedMs
            };
        }
    }
}
