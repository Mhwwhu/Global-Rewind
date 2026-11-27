using System;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Logging;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 用游戏自己的联机校验和当"恢复是否等价"的判据。
/// 与 NetFullCombatState 完全一致：双方校验和相同，说明模型层没有分歧。
///
/// 注意：校验和覆盖不到 RoundNumber / CurrentSide / 怪物 AI / Power 内部数据，
/// 所以它只能证明"没分歧"，不能证明"完全等价"；这些差异由快照本身负责。
/// </summary>
internal static class CombatChecksum
{
    public static bool TryCompute(out uint checksum)
    {
        return ComputePure(out checksum);
    }

    /// <summary>
    /// 纯哈希：只对 `NetFullCombatState` 做 XxHash32，**不推进** `ChecksumTracker.NextId`、
    /// 也不向对端发 `ChecksumDataMessage`。
    ///
    /// 多人局里这一点是硬要求：`ChecksumTracker.GenerateChecksum(context, action)` 是
    /// “生成并记账”的重载，双方必须调用同样次数；模组自己每次抓点/跳转都调它，
    /// 就会凭空多出一次校验和，可能触发 `StateDivergence`。
    /// 这里只走 `GenerateChecksum(NetFullCombatState)` 的纯哈希重载。
    /// </summary>
    public static bool ComputePure(out uint checksum)
    {
        checksum = 0;

        if (!TryBuildNormalized(out NetFullCombatState state, out string error))
        {
            if (!string.IsNullOrEmpty(error))
            {
                ModLog.Warn($"checksum failed: {error}");
            }

            return false;
        }

        try
        {
            checksum = RunManager.Instance.ChecksumTracker.GenerateChecksum(state);
            return true;
        }
        catch (Exception e)
        {
            ModLog.Warn($"checksum failed: {e.GetType().Name} {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 规范化后的 NetFullCombatState：选择/奖励的 id 计数器属于同步簿记，不在快照范围内，
    /// 而且只增不减；不归一化会让任何一次选牌提示都导致校验和永久偏移。
    /// </summary>
    public static bool TryBuildNormalized(out NetFullCombatState state, out string error)
    {
        state = null;
        error = null;

        try
        {
            RunManager manager = RunManager.Instance;
            if (manager == null || manager.DebugOnlyGetState() == null || manager.ChecksumTracker == null)
            {
                return false;
            }

            state = NetFullCombatState.FromRun(manager.DebugOnlyGetState(), null);
            state.nextChoiceIds?.Clear();
            state.nextRewardIds?.Clear();
            state.lastExecutedActionId = null;
            state.lastExecutedHookId = null;
            return true;
        }
        catch (Exception e)
        {
            error = $"{e.GetType().Name} {e.Message}";
            return false;
        }
    }

    /// <summary>
    /// 可读摘要：校验和只给一个数，出问题时要靠这个定位"到底哪一块没恢复"。
    /// 只放会影响玩法的字段，顺序固定。
    /// </summary>
    public static string BuildDigest(NetFullCombatState state)
    {
        if (state == null)
        {
            return "<null>";
        }

        var sb = new StringBuilder(1024);

        sb.AppendLine("run.rng:");
        foreach (RunRngType type in Enum.GetValues<RunRngType>())
        {
            if (state.Rng?.Rngs != null && state.Rng.Rngs.TryGetValue(type, out var rng))
            {
                sb.AppendLine($"  {type} c={rng.counter} s0={rng.state0:x16}");
            }
        }

        sb.AppendLine("creatures:");
        if (state.Creatures != null)
        {
            foreach (var creature in state.Creatures)
            {
                sb.Append("  ");
                sb.Append(creature.monsterId?.Entry ?? creature.playerId?.ToString() ?? "?");
                sb.Append($" hp={creature.currentHp}/{creature.maxHp} block={creature.block} powers=");
                if (creature.powers != null)
                {
                    foreach (var power in creature.powers)
                    {
                        sb.Append($"{power.id.Entry}:{power.amount},");
                    }
                }

                sb.AppendLine();
            }
        }

        sb.AppendLine("players:");
        if (state.Players != null)
        {
            foreach (var player in state.Players)
            {
                sb.Append($"  {player.characterId?.Entry} turn={player.turnNumber} phase={player.phase} ");
                sb.Append($"energy={player.energy} stars={player.stars} gold={player.gold} ");

                if (player.piles != null)
                {
                    foreach (var pile in player.piles)
                    {
                        sb.Append($"{pile.pileType}={pile.cards?.Count ?? 0} ");
                    }
                }

                sb.AppendLine();

                if (player.piles == null)
                {
                    continue;
                }

                foreach (var pile in player.piles)
                {
                    if (pile.cards == null)
                    {
                        continue;
                    }

                    sb.Append($"    {pile.pileType}: ");
                    foreach (var card in pile.cards)
                    {
                        sb.Append(card.card?.Id?.Entry ?? "?");
                        if (card.card != null && card.card.CurrentUpgradeLevel > 0)
                        {
                            sb.Append($"+{card.card.CurrentUpgradeLevel}");
                        }

                        sb.Append(',');
                    }

                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }
}
