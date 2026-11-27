using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Players;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 多人事件的“时间坐标”：不是墙上时间，而是所有确定性计数器的当前位置。
///
/// 单人局它就是 `NextActionId + Round + Turn` 的退化形式；
/// 多人局还要带上每个座位的 choice 计数器、每个玩家的阶段与回合号、以及
/// “谁已经结束回合 / 谁在额外回合里”这些流程集合。
///
/// 两个节点只要 <see cref="Frontier"/> 不同，就绝不能当成同一个状态合并。
/// 本地玩家的 NetId 只用于展示，不参与比较（各端本来就不一样）。
/// </summary>
public sealed class Frontier
{
    [JsonPropertyName("next_action_id")]
    public uint NextActionId { get; set; }

    [JsonPropertyName("next_hook_id")]
    public uint NextHookId { get; set; }

    [JsonPropertyName("next_choice_ids")]
    public uint[] NextChoiceIds { get; set; } = Array.Empty<uint>();

    [JsonPropertyName("round")]
    public int RoundNumber { get; set; }

    [JsonPropertyName("side")]
    public int Side { get; set; }

    [JsonPropertyName("turn_numbers")]
    public int[] TurnNumbers { get; set; } = Array.Empty<int>();

    [JsonPropertyName("phases")]
    public int[] Phases { get; set; } = Array.Empty<int>();

    [JsonPropertyName("ready_to_end_turn")]
    public ulong[] ReadyToEndTurn { get; set; } = Array.Empty<ulong>();

    [JsonPropertyName("ready_to_begin_enemy_turn")]
    public ulong[] ReadyToBeginEnemyTurn { get; set; } = Array.Empty<ulong>();

    [JsonPropertyName("taking_extra_turn")]
    public ulong[] TakingExtraTurn { get; set; } = Array.Empty<ulong>();

    [JsonPropertyName("local_net_id")]
    public ulong LocalNetId { get; set; }

    public static Frontier Capture(CombatState combat)
    {
        var frontier = new Frontier
        {
            LocalNetId = PlayerScope.LocalNetId
        };

        try
        {
            RunManager manager = RunManager.Instance;
            ActionQueueSet queueSet = manager?.ActionQueueSet;
            ActionQueueSynchronizer actionSynchronizer = manager?.ActionQueueSynchronizer;
            PlayerChoiceSynchronizer choiceSynchronizer = manager?.PlayerChoiceSynchronizer;

            frontier.NextActionId = queueSet?.NextActionId ?? 0;
            frontier.NextHookId = actionSynchronizer?.NextHookId ?? 0;
            frontier.NextChoiceIds = choiceSynchronizer?.ChoiceIds?.ToArray() ?? Array.Empty<uint>();
        }
        catch
        {
            // 计数器读不到时保留 0；后续比较会自然判为不同。
        }

        if (combat == null)
        {
            return frontier;
        }

        frontier.RoundNumber = combat.RoundNumber;
        frontier.Side = (int)combat.CurrentSide;
        frontier.TurnNumbers = combat.Players
            .Select(player => player?.PlayerCombatState?.TurnNumber ?? 0)
            .ToArray();
        frontier.Phases = combat.Players
            .Select(player => (int)(player?.PlayerCombatState?.Phase ?? PlayerTurnPhase.None))
            .ToArray();

        frontier.ReadyToEndTurn = ReadNetIds("PlayersReadyToEndTurn");
        frontier.ReadyToBeginEnemyTurn = ReadNetIds("PlayersReadyToBeginEnemyTurn");
        frontier.TakingExtraTurn = ReadNetIds("PlayersTakingExtraTurn");
        return frontier;
    }

    private static ulong[] ReadNetIds(string propertyName)
    {
        return TurnStateProbe.ReadPlayers(propertyName)
            .Where(player => player != null)
            .Select(player => player.NetId)
            .OrderBy(id => id)
            .ToArray();
    }

    /// <summary>
    /// 只比“可玩状态”：回合、侧、每人回合号/阶段、就绪集合、额外回合。
    ///
    /// 刻意**不比**确定性计数器（NextActionId / NextHookId / NextChoiceIds）：它们只是给未来动作
    /// 预分配的编号，不影响当前局面。换序重放走到同一局面时，计数器可能不同（合并/排队路径不同），
    /// 但玩家看到的状态一模一样 —— 合并判据用这个宽松口径，跳转后由快照把计数器整体写回。
    /// </summary>
    public bool SameStateAs(Frontier other)
    {
        if (other == null)
        {
            return false;
        }

        return RoundNumber == other.RoundNumber
               && Side == other.Side
               && TurnNumbers.SequenceEqual(other.TurnNumbers)
               && Phases.SequenceEqual(other.Phases)
               && ReadyToEndTurn.SequenceEqual(other.ReadyToEndTurn)
               && ReadyToBeginEnemyTurn.SequenceEqual(other.ReadyToBeginEnemyTurn)
               && TakingExtraTurn.SequenceEqual(other.TakingExtraTurn);
    }

    /// <summary>
    /// 逻辑坐标是否相同。刻意不比较 <see cref="LocalNetId"/>：
    /// 同一个逻辑状态在不同端上的本地玩家 ID 本来就不一样。
    /// </summary>
    public bool SameAs(Frontier other)
    {
        if (other == null)
        {
            return false;
        }

        return NextActionId == other.NextActionId
               && NextHookId == other.NextHookId
               && RoundNumber == other.RoundNumber
               && Side == other.Side
               && SameArray(NextChoiceIds, other.NextChoiceIds)
               && SameArray(TurnNumbers, other.TurnNumbers)
               && SameArray(Phases, other.Phases)
               && SameArray(ReadyToEndTurn, other.ReadyToEndTurn)
               && SameArray(ReadyToBeginEnemyTurn, other.ReadyToBeginEnemyTurn)
               && SameArray(TakingExtraTurn, other.TakingExtraTurn);
    }

    private static bool SameArray<T>(T[] left, T[] right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null || left.Length != right.Length)
        {
            return false;
        }

        for (int i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    public string Describe()
    {
        string choices = string.Join(",", NextChoiceIds ?? Array.Empty<uint>());
        string turns = string.Join(",", TurnNumbers ?? Array.Empty<int>());
        string phases = string.Join(",", (Phases ?? Array.Empty<int>())
            .Select(value => ((PlayerTurnPhase)value).ToString()));
        string ready = string.Join(",", ReadyToEndTurn ?? Array.Empty<ulong>());
        string extra = string.Join(",", TakingExtraTurn ?? Array.Empty<ulong>());

        return $"a{NextActionId} h{NextHookId} c[{choices}] r{RoundNumber} "
               + $"{(CombatSide)Side} t[{turns}] p[{phases}] ready[{ready}] extra[{extra}]";
    }

    // ---------------------------------------------------------------- 网络传输

    /// <summary>
    /// 跨端传输：只写逻辑字段，<see cref="LocalNetId"/> 由接收端自己填
    /// （同一个状态在不同端上的本地玩家 id 本来就不一样）。
    /// </summary>
    public void Write(PacketWriter writer)
    {
        writer.WriteUInt(NextActionId);
        writer.WriteUInt(NextHookId);
        WriteUInts(writer, NextChoiceIds);
        writer.WriteInt(RoundNumber);
        writer.WriteInt(Side);
        WriteInts(writer, TurnNumbers);
        WriteInts(writer, Phases);
        WriteULongs(writer, ReadyToEndTurn);
        WriteULongs(writer, ReadyToBeginEnemyTurn);
        WriteULongs(writer, TakingExtraTurn);
    }

    public static Frontier Read(PacketReader reader)
    {
        return new Frontier
        {
            NextActionId = reader.ReadUInt(),
            NextHookId = reader.ReadUInt(),
            NextChoiceIds = ReadUInts(reader),
            RoundNumber = reader.ReadInt(),
            Side = reader.ReadInt(),
            TurnNumbers = ReadInts(reader),
            Phases = ReadInts(reader),
            ReadyToEndTurn = ReadULongs(reader),
            ReadyToBeginEnemyTurn = ReadULongs(reader),
            TakingExtraTurn = ReadULongs(reader),
            LocalNetId = PlayerScope.LocalNetId
        };
    }

    private static void WriteUInts(PacketWriter writer, uint[] values)
    {
        values ??= Array.Empty<uint>();
        writer.WriteInt(values.Length);
        foreach (uint value in values)
        {
            writer.WriteUInt(value);
        }
    }

    private static uint[] ReadUInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<uint>();
        }

        var values = new uint[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadUInt();
        }

        return values;
    }

    private static void WriteInts(PacketWriter writer, int[] values)
    {
        values ??= Array.Empty<int>();
        writer.WriteInt(values.Length);
        foreach (int value in values)
        {
            writer.WriteInt(value);
        }
    }

    private static int[] ReadInts(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<int>();
        }

        var values = new int[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadInt();
        }

        return values;
    }

    private static void WriteULongs(PacketWriter writer, ulong[] values)
    {
        values ??= Array.Empty<ulong>();
        writer.WriteInt(values.Length);
        foreach (ulong value in values)
        {
            writer.WriteULong(value);
        }
    }

    private static ulong[] ReadULongs(PacketReader reader)
    {
        int count = reader.ReadInt();
        if (count <= 0 || count > 64)
        {
            return Array.Empty<ulong>();
        }

        var values = new ulong[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = reader.ReadULong();
        }

        return values;
    }

    public Dictionary<string, object> ToDictionary()
    {
        return new Dictionary<string, object>
        {
            ["next_action_id"] = NextActionId,
            ["next_hook_id"] = NextHookId,
            ["next_choice_ids"] = NextChoiceIds,
            ["round"] = RoundNumber,
            ["side"] = ((CombatSide)Side).ToString(),
            ["turn_numbers"] = TurnNumbers,
            ["phases"] = (Phases ?? Array.Empty<int>())
                .Select(value => ((PlayerTurnPhase)value).ToString())
                .ToArray(),
            ["ready_to_end_turn"] = ReadyToEndTurn,
            ["ready_to_begin_enemy_turn"] = ReadyToBeginEnemyTurn,
            ["taking_extra_turn"] = TakingExtraTurn,
            ["local_net_id"] = LocalNetId
        };
    }
}
