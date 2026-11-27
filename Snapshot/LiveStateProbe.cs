using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.FoolArrows;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rngs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Players;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 把"当前活着的游戏状态"整理成可机器比对的 JSON 结构。
///
/// 用途：在跳转前后各取一次，两者必须逐字相同 —— 这比"看起来对了"可靠。
/// 覆盖范围对齐游戏自己的联机同步包（NetFullCombatState）：
///   RNG（每个 RunRngType 的 counter + 状态）、玩家（血量/格挡/能量/星/回合/阶段/
///   牌堆内容/药水/遗物+计数器/球/箭矢/玩家 RNG）、生物（血量/格挡/Power）、敌人下一步动作与意图。
/// </summary>
internal static class LiveStateProbe
{
    public static Dictionary<string, object> Capture()
    {
        var data = new Dictionary<string, object>();
        IRunState state = RunManager.Instance?.DebugOnlyGetState();
        if (state == null)
        {
            data["error"] = "没有正在进行的 run";
            return data;
        }

        data["seed"] = state.Rng?.StringSeed ?? string.Empty;
        data["act"] = state.CurrentActIndex;
        data["floor"] = state.TotalFloor;
        data["rng"] = DumpRunRng(state.Rng);
        data["turn_state"] = DumpTurnState();
        data["players"] = DumpPlayers(state);
        data["combat"] = DumpCombat(state);
        return data;
    }

    /// <summary>
    /// 回合流程状态（CombatTurnState 是 internal 类型，只能反射读）：
    /// 结束回合就绪集合 / 不能撤销的玩家 / 额外回合名单（PaelsEye）/ 侧切换标志。
    /// 这些不在 RunState 里，但决定了按钮能不能按、牌能不能打。
    /// </summary>
    private static Dictionary<string, object> DumpTurnState()
    {
        var dump = new Dictionary<string, object>();
        try
        {
            FieldInfo field = typeof(CombatManager).GetField(
                "_turnState", BindingFlags.Instance | BindingFlags.NonPublic);
            object turnState = field?.GetValue(CombatManager.Instance);
            if (turnState == null)
            {
                dump["active"] = false;
                return dump;
            }

            Type type = turnState.GetType();
            dump["active"] = true;
            dump["in_progress"] = ReadBool(type, turnState, "IsInProgress");
            dump["starting"] = ReadBool(type, turnState, "IsStarting");
            dump["enemy_turn_started"] = ReadBool(type, turnState, "IsEnemyTurnStarted");
            dump["ending_phase_one"] = ReadBool(type, turnState, "EndingPlayerTurnPhaseOne");
            dump["ending_phase_two"] = ReadBool(type, turnState, "EndingPlayerTurnPhaseTwo");
            dump["ready_to_end"] = ReadPlayers(type, turnState, "PlayersReadyToEndTurn");
            dump["cannot_unend"] = ReadPlayers(type, turnState, "PlayersThatCannotUnEndTurn");
            dump["ready_to_begin_enemy"] = ReadPlayers(type, turnState, "PlayersReadyToBeginEnemyTurn");
            dump["extra_turn"] = ReadPlayers(type, turnState, "PlayersTakingExtraTurn");
        }
        catch (Exception e)
        {
            dump["error"] = e.GetType().Name + ": " + e.Message;
        }

        return dump;
    }

    private static bool ReadBool(Type type, object target, string property)
    {
        try
        {
            PropertyInfo info = type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return info?.GetValue(target) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> ReadPlayers(Type type, object target, string property)
    {
        var names = new List<string>();
        try
        {
            PropertyInfo info = type.GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (info?.GetValue(target) is not System.Collections.IEnumerable items)
            {
                return names;
            }

            foreach (object item in items)
            {
                if (item is Player player)
                {
                    names.Add($"{player.NetId}:{player.Character?.Id.Entry}");
                }
                else if (item != null)
                {
                    names.Add(item.GetType().Name);
                }
            }

            names.Sort(StringComparer.Ordinal);
        }
        catch
        {
            // 读不到就当空集合
        }

        return names;
    }

    private static Dictionary<string, object> DumpRunRng(RunRngSet rngSet)
    {
        var dump = new Dictionary<string, object>();
        if (rngSet == null)
        {
            return dump;
        }

        foreach (RunRngType type in Enum.GetValues<RunRngType>())
        {
            try
            {
                dump[type.ToString()] = DumpRng(rngSet.GetRng(type));
            }
            catch (Exception e)
            {
                dump[type.ToString()] = "error:" + e.GetType().Name;
            }
        }

        return dump;
    }

    private static string DumpRng(Rng rng)
    {
        if (rng == null)
        {
            return "null";
        }

        // counter + 4 个状态字就是这颗 RNG 的全部可变状态
        var serial = rng.ToSerializable();
        return $"{serial.counter}:{serial.state0:x16}:{serial.state1:x16}:{serial.state2:x16}:{serial.state3:x16}";
    }

    private static List<object> DumpPlayers(IRunState state)
    {
        var players = new List<object>();
        foreach (Player player in state.Players)
        {
            if (player == null)
            {
                continue;
            }

            var item = new Dictionary<string, object>
            {
                ["net_id"] = player.NetId,
                ["slot"] = state.GetPlayerSlotIndex(player),
                ["local"] = PlayerScope.IsLocal(player),
                ["dead"] = player.Creature?.IsDead ?? false,
                ["ready"] = CombatManager.Instance?.IsPlayerReadyToEndTurn(player) ?? false,
                ["character"] = player.Character?.Id.Entry,
                ["gold"] = player.Gold,
                ["max_energy"] = player.MaxEnergy,
                ["player_rng"] = DumpPlayerRng(player.PlayerRng),
                ["relics"] = DumpRelics(player),
                ["potion_slots"] = DumpPotionSlots(player),
                ["creature"] = DumpCreature(player.Creature)
            };

            PlayerCombatState combat = player.PlayerCombatState;
            if (combat != null)
            {
                item["turn"] = combat.TurnNumber;
                item["phase"] = combat.Phase.ToString();
                item["energy"] = combat.Energy;
                item["stars"] = combat.Stars;
                item["piles"] = DumpPiles(combat);
                item["orbs"] = DumpOrbs(combat);
                item["arrows"] = DumpArrows(combat);
            }

            players.Add(item);
        }

        return players;
    }

    private static Dictionary<string, object> DumpPlayerRng(PlayerRngSet rngSet)
    {
        var dump = new Dictionary<string, object>();
        if (rngSet == null)
        {
            return dump;
        }

        foreach (PlayerRngType type in Enum.GetValues<PlayerRngType>())
        {
            try
            {
                dump[type.ToString()] = DumpRng(rngSet.GetRng(type));
            }
            catch (Exception e)
            {
                dump[type.ToString()] = "error:" + e.GetType().Name;
            }
        }

        return dump;
    }

    private static List<object> DumpRelics(Player player)
    {
        var relics = new List<object>();
        foreach (RelicModel relic in player.Relics)
        {
            if (relic == null)
            {
                continue;
            }

            relics.Add(new Dictionary<string, object>
            {
                ["id"] = relic.Id.Entry,
                ["counter"] = relic.ShowCounter ? relic.DisplayAmount : (int?)null
            });
        }

        return relics;
    }

    private static List<object> DumpPotionSlots(Player player)
    {
        var slots = new List<object>();
        foreach (PotionModel potion in player.PotionSlots)
        {
            slots.Add(potion == null ? null : potion.Id.Entry);
        }

        return slots;
    }

    private static Dictionary<string, object> DumpPiles(PlayerCombatState combat)
    {
        var piles = new Dictionary<string, object>();
        foreach (CardPile pile in combat.AllPiles)
        {
            var cards = new List<string>();
            foreach (CardModel card in pile.Cards)
            {
                if (card == null)
                {
                    continue;
                }

                string id = card.Id.Entry;
                if (card.CurrentUpgradeLevel > 0)
                {
                    id += "+" + card.CurrentUpgradeLevel;
                }

                cards.Add(id);
            }

            piles[pile.Type.ToString()] = cards;
        }

        return piles;
    }

    private static Dictionary<string, object> DumpOrbs(PlayerCombatState combat)
    {
        var dump = new Dictionary<string, object>
        {
            ["capacity"] = combat.OrbQueue?.Capacity ?? 0
        };

        var orbs = new List<object>();
        if (combat.OrbQueue != null)
        {
            foreach (OrbModel orb in combat.OrbQueue.Orbs)
            {
                orbs.Add(orb == null
                    ? null
                    : new Dictionary<string, object>
                    {
                        ["id"] = orb.Id.Entry,
                        ["passive"] = orb.PassiveVal,
                        ["evoke"] = orb.EvokeVal
                    });
            }
        }

        dump["items"] = orbs;
        return dump;
    }

    private static List<string> DumpArrows(PlayerCombatState combat)
    {
        var arrows = new List<string>();
        foreach (ArrowSlot slot in combat.Arrows)
        {
            arrows.Add(slot?.Status.ToString() ?? "null");
        }

        return arrows;
    }

    private static Dictionary<string, object> DumpCombat(IRunState state)
    {
        var dump = new Dictionary<string, object>();
        if (state.CurrentRoom is not CombatRoom room || room.CombatState == null)
        {
            dump["active"] = false;
            return dump;
        }

        CombatState combat = room.CombatState;
        dump["active"] = true;
        dump["round"] = combat.RoundNumber;
        dump["side"] = combat.CurrentSide.ToString();
        dump["encounter"] = combat.Encounter?.Id.Entry;

        var enemies = new List<object>();
        foreach (Creature enemy in combat.Enemies)
        {
            if (enemy == null)
            {
                continue;
            }

            var item = DumpCreature(enemy);
            item["move"] = enemy.Monster?.NextMove?.Id;
            item["intents"] = DumpIntents(enemy, combat);
            enemies.Add(item);
        }

        dump["enemies"] = enemies;
        return dump;
    }

    /// <summary>敌人头顶那几个图标的内容，用于确认"模型意图"和"UI 意图"是否一致。</summary>
    private static List<string> DumpIntents(Creature enemy, CombatState combat)
    {
        var intents = new List<string>();
        MonsterModel monster = enemy.Monster;
        if (monster?.NextMove?.Intents == null)
        {
            return intents;
        }

        var targets = new List<Creature>();
        foreach (Player player in combat.Players)
        {
            if (player?.Creature != null)
            {
                targets.Add(player.Creature);
            }
        }

        foreach (AbstractIntent intent in monster.NextMove.Intents)
        {
            if (intent == null)
            {
                intents.Add("null");
                continue;
            }

            string label = null;
            try
            {
                label = intent.GetIntentLabel(targets, enemy).GetRawText();
            }
            catch
            {
                // 有些意图的文案要靠具体战斗上下文，取不到就只报类型
            }

            intents.Add(intent.IntentType + (string.IsNullOrEmpty(label) ? string.Empty : ":" + label));
        }

        return intents;
    }

    private static Dictionary<string, object> DumpCreature(Creature creature)
    {
        var dump = new Dictionary<string, object>();
        if (creature == null)
        {
            return dump;
        }

        dump["name"] = creature.Name;
        dump["hp"] = creature.CurrentHp;
        dump["max_hp"] = creature.MaxHp;
        dump["block"] = creature.Block;
        dump["dead"] = creature.IsDead;
        dump["powers"] = DumpPowers(creature);
        return dump;
    }

    private static List<string> DumpPowers(Creature creature)
    {
        var powers = new List<string>();
        if (creature.Powers == null)
        {
            return powers;
        }

        foreach (PowerModel power in creature.Powers)
        {
            if (power != null)
            {
                powers.Add(power.Id.Entry + ":" + power.Amount);
            }
        }

        return powers;
    }

    /// <summary>人读版：把状态压成几行文本，日志里一眼能看出差异。</summary>
    public static string Describe(Dictionary<string, object> state)
    {
        var sb = new StringBuilder(512);
        foreach (KeyValuePair<string, object> entry in state)
        {
            sb.Append(entry.Key).Append('=').Append(entry.Value).AppendLine();
        }

        return sb.ToString();
    }
}
