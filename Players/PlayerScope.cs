using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace GlobalRewind.Players;

/// <summary>
/// 所有“按人取状态”的唯一入口。
///
/// 单人局 `Players[0]` 就是本地玩家；多人局不一定（座位顺序由大厅决定）。
/// 所以业务代码一律走这里，不再自己写 `Players[0]`：
///   · <see cref="Local"/>：本地玩家（优先 LocalContext，拿不到才退回 0 号位）；
///   · <see cref="All"/>：全体玩家（共享状态并集的来源）；
///   · <see cref="ActivePlayers"/>：本回合真正要行动的人（额外回合名单优先，死亡/未参与者不算）。
///
/// “假多人”= NetService 仍是 Singleplayer、但 RunState 里有多个 Player（bootstrapper 的 dummy player）。
/// 它既不是真联机，也不能再按单人逻辑读 0 号位，所以单独区分。
/// </summary>
internal static class PlayerScope
{
    public static IRunState RunState
    {
        get
        {
            try
            {
                return RunManager.Instance?.DebugOnlyGetState();
            }
            catch
            {
                return null;
            }
        }
    }

    public static IReadOnlyList<Player> All
    {
        get
        {
            IRunState state = RunState;
            return state?.Players ?? Array.Empty<Player>();
        }
    }

    public static Player Local
    {
        get
        {
            IRunState state = RunState;
            if (state == null)
            {
                return null;
            }

            try
            {
                Player me = LocalContext.GetMe(state);
                if (me != null)
                {
                    return me;
                }
            }
            catch
            {
                // LocalContext 找不到本地玩家时不要炸，退回 0 号位。
            }

            return state.Players.Count > 0 ? state.Players[0] : null;
        }
    }

    public static ulong LocalNetId
    {
        get
        {
            try
            {
                return LocalContext.NetId ?? 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    public static NetGameType NetType
    {
        get
        {
            try
            {
                return RunManager.Instance?.NetService?.Type ?? NetGameType.None;
            }
            catch
            {
                return NetGameType.None;
            }
        }
    }

    /// <summary>真正的联机局（Host / Client）。单人 + dummy player 不算。</summary>
    public static bool IsMultiplayer =>
        NetType == NetGameType.Host || NetType == NetGameType.Client;

    /// <summary>单人局里塞了多个 Player（测试用）。</summary>
    public static bool IsFakeMultiplayer => !IsMultiplayer && All.Count > 1;

    /// <summary>所有“必须按多人语义处理”的局：真联机 + 假多人。</summary>
    public static bool IsMultiplayerLike => IsMultiplayer || IsFakeMultiplayer;

    public static bool IsLocal(Player player)
    {
        return player != null && LocalNetId != 0 && player.NetId == LocalNetId;
    }

    public static int SlotOf(Player player)
    {
        IRunState state = RunState;
        if (state == null || player == null)
        {
            return -1;
        }

        try
        {
            return state.GetPlayerSlotIndex(player);
        }
        catch
        {
            return -1;
        }
    }

    public static CombatState CombatState
    {
        get
        {
            Player me = Local;
            if (me?.Creature?.CombatState is CombatState local)
            {
                return local;
            }

            foreach (Player player in All)
            {
                if (player?.Creature?.CombatState is CombatState combat)
                {
                    return combat;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// 本回合真正要行动的人：
    ///   · 额外回合名单非空 → 只有名单里的人（PaelsEye 之类）；
    ///   · 否则 → 全体存活玩家。
    /// 死亡/未参与者视为已就绪，不阻塞“全体可操作”的判定 —— 这和游戏 StartTurn 里
    /// “没在起始名单里的玩家自动 SetReadyToEndTurn”是同一套语义。
    /// </summary>
    public static IReadOnlyList<Player> ActivePlayers(CombatState combat)
    {
        if (combat == null)
        {
            return Array.Empty<Player>();
        }

        IReadOnlyList<Player> extra = TurnStateProbe.ReadPlayers("PlayersTakingExtraTurn");
        IEnumerable<Player> source = extra != null && extra.Count > 0 ? extra : combat.Players;

        return source
            .Where(player => player != null && player.Creature != null && !player.Creature.IsDead)
            .ToList();
    }

    public static bool IsReadyToEndTurn(Player player)
    {
        try
        {
            return CombatManager.Instance?.IsPlayerReadyToEndTurn(player) ?? false;
        }
        catch
        {
            return false;
        }
    }

    public static string Describe(Player player)
    {
        if (player == null)
        {
            return "?";
        }

        string character = player.Character?.Id.Entry ?? "?";
        return $"{player.NetId}:{character}";
    }
}
