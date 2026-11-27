using System;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Players;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 多人局的“全体静止点”探针。
///
/// 单人局的稳定点只看本地玩家；多人局不行：A 可以出牌、B 还在选牌、C 的回合开始 hook 还在跑。
/// 这里要求所有活跃玩家同时满足：
///   · Phase == Play（官方定义的“允许手动打牌/用药水”阶段）；
///   · 没有已结束回合（`IsPlayerReadyToEndTurn` 为 false）；
///   · 本地 `PlayerActionsDisabled` 为 false（结束回合过渡中）；
///   · 所有玩家队列为空（`ActionQueueSet.IsEmpty`）；
///   · 执行器没有正在跑的动作、没有被暂停；
///   · 没有 pending player choice、没有未结算 hook、没有等玩家回合的延迟动作；
///   · 回合流程没有卡在结束回合的第一/第二阶段。
///
/// 只有这些同时成立，快照才是“所有人都还能操作、且没有任何东西在飞”的共同状态。
/// 任何一条不满足都只写 capture_skipped，不建点。
/// </summary>
internal static class QuiescenceProbe
{
    /// <param name="ignoreLocalInputBlock">
    /// 协同跳转冻结时会主动把本地 `PlayerActionsDisabled` 置 true（挡住手牌/药水/结束回合），
    /// 这只是本机视角的输入锁，不代表回合在过渡。此时必须忽略这一条，
    /// 否则“冻结”本身会让探针永远失败（M2 实测踩过）。
    /// </param>
    public static bool TryCapture(out Frontier frontier, out string reason,
        bool ignoreLocalInputBlock = false)
    {
        return TryCaptureCore(null, out frontier, out reason, ignoreLocalInputBlock);
    }

    /// <summary>
    /// **动作边界**判据：一个动作的逻辑刚刚执行完、下一个动作还没开始的瞬间。
    ///
    /// 和静止点的区别只有两处：
    ///   · 允许队列里还有排队中的动作 —— 它们是“未来”，不属于当前状态；
    ///   · 允许执行器的 `CurrentlyRunningAction` 还是这个刚结算的动作（官方在
    ///     `AfterActionExecuted` 之后才把它置空）。
    /// 其余条件（玩家回合、所有人 Phase=Play、未结束回合、没有 hook / choice 在飞、
    /// 本地输入未禁用）一条都不放松 —— 这样抓到的仍然是“逻辑上完整、可操作”的状态，
    /// 只是不再等动画/队列全部排空。
    /// </summary>
    public static bool TryCaptureBoundary(GameAction finishing, out Frontier frontier, out string reason,
        bool ignoreLocalInputBlock = false)
    {
        return TryCaptureCore(finishing, out frontier, out reason, ignoreLocalInputBlock);
    }

    private static bool TryCaptureCore(GameAction boundaryAction, out Frontier frontier, out string reason,
        bool ignoreLocalInputBlock)
    {
        frontier = null;
        bool boundary = boundaryAction != null;

        CombatManager manager = CombatManager.Instance;
        if (manager == null || !manager.IsInProgress)
        {
            reason = "战斗不在进行中";
            return false;
        }

        CombatState combat = PlayerScope.CombatState;
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

        if (TurnStateProbe.ReadBool("EndingPlayerTurnPhaseOne") ||
            TurnStateProbe.ReadBool("EndingPlayerTurnPhaseTwo"))
        {
            reason = "回合结束过渡中";
            return false;
        }

        // 先做最便宜的队列/执行器判断：延迟抓点每帧都会探一次，
        // 对端还在跑动作时不应该每帧去做 per-player 反射。
        RunManager run = RunManager.Instance;
        if (run == null)
        {
            reason = "RunManager 不可用";
            return false;
        }

        if (!boundary && (run.ActionQueueSet == null || !run.ActionQueueSet.IsEmpty))
        {
            reason = "还有动作在队列里没结算";
            return false;
        }

        if (run.ActionQueueSet == null)
        {
            reason = "ActionQueueSet 不可用";
            return false;
        }

        if (run.ActionExecutor == null)
        {
            reason = "ActionExecutor 不可用";
            return false;
        }

        GameAction running = run.ActionExecutor.CurrentlyRunningAction;
        if (running != null && (!boundary || running != boundaryAction))
        {
            reason = $"动作正在执行：{running.GetType().Name}";
            return false;
        }

        if (run.ActionExecutor.IsPaused)
        {
            reason = "动作执行器处于暂停状态";
            return false;
        }

        var active = PlayerScope.ActivePlayers(combat);
        if (active.Count == 0)
        {
            reason = "没有存活的活跃玩家";
            return false;
        }

        foreach (Player player in active)
        {
            if (player.PlayerCombatState == null)
            {
                reason = $"{PlayerScope.Describe(player)} 没有 PlayerCombatState";
                return false;
            }

            PlayerTurnPhase phase = player.PlayerCombatState.Phase;
            if (phase != PlayerTurnPhase.Play)
            {
                reason = $"{PlayerScope.Describe(player)} 阶段为 {phase}（非 Play）";
                return false;
            }

            if (manager.IsPlayerReadyToEndTurn(player))
            {
                reason = $"{PlayerScope.Describe(player)} 已结束回合";
                return false;
            }
        }

        if (!ignoreLocalInputBlock && manager.PlayerActionsDisabled)
        {
            reason = "本地玩家动作被禁用（结束回合过渡中）";
            return false;
        }

        if (run.PlayerChoiceSynchronizer != null &&
            TurnStateProbe.CountCollection(run.PlayerChoiceSynchronizer, "_receivedChoices") > 0)
        {
            reason = "有玩家选择还没结算";
            return false;
        }

        if (run.ActionQueueSynchronizer != null)
        {
            if (TurnStateProbe.CountCollection(run.ActionQueueSynchronizer, "_hookActions") > 0)
            {
                reason = "有 hook 动作还没结算";
                return false;
            }

            if (TurnStateProbe.CountCollection(
                    run.ActionQueueSynchronizer, "_requestedActionsWaitingForPlayerTurn") > 0)
            {
                reason = "有动作在等玩家回合";
                return false;
            }
        }

        frontier = Frontier.Capture(combat);
        reason = null;
        return true;
    }

    public static bool IsQuiescent
    {
        get
        {
            return TryCapture(out _, out _);
        }
    }
}
