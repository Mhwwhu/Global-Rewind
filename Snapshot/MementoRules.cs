using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 哪些字段/类型可以进快照的判定。
///
/// 原则：
///   · 游戏模型（RunState / CombatState / Player / Creature / CardModel / PowerModel ...）全部可进；
///   · Godot 对象、委托、Task、线程原语、反射元数据、logger 一律不进（不遍历也不写回）；
///   · canonical（不可变）模型只存引用、不深入，避免把整份内容数据库拖进快照；
///   · CombatTurnState 走白名单：只存"回合流程的数据"（结束回合就绪集合、额外回合名单、
///     侧切换标志），不碰 ReadyLock / TaskCompletionSource / CancellationTokenSource。
///     不存这些数据，"结束回合"按下的状态和 PaelsEye 的额外回合就恢复不出来。
/// </summary>
internal static class MementoRules
{
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache =
        new ConcurrentDictionary<Type, FieldInfo[]>();

    /// <summary>
    /// 按声明类型的字段白名单。键是类型 FullName，值是允许进快照的字段名（子串匹配，
    /// 便于命中自动属性的 <Name>k__BackingField）。命中表内的类型允许遍历，但只记这些字段，
    /// 绝不顺着网络服务 / 消息缓冲 / 事件委托 / Task 往下走。
    ///
    /// 前四个是官方 CombatReplayWriter.RecordInitialState 记录的确定性计数器：
    /// choiceIds / nextActionId / nextHookId / nextChecksumId。后续动作编号、hook 编号、
    /// 玩家选择编号都从它们派生；不恢复，"跳转到节点"就不是"从战斗开始重放到该节点"。
    /// 最后一个是 CombatManager 的玩家动作禁用标志，与回合流程一起决定能否出牌。
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string[]> RestrictedFields =
        new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // 回合流程：结束回合就绪集合、额外回合名单（PaelsEye）、侧切换标志。
            // 其余字段是 ReadyLock / CTS / TCS，写回会打断正在等待的 turn loop。
            ["MegaCrit.Sts2.Core.Combat.CombatTurnState"] = new[]
            {
                "PlayersReadyToEndTurn",
                "PlayersThatCannotUnEndTurn",
                "PlayersReadyToBeginEnemyTurn",
                "PlayersTakingExtraTurn",
                "PendingLoss",
                "IsInProgress",
                "IsStarting",
                "IsEnemyTurnStarted",
                "EndingPlayerTurnPhaseOne",
                "EndingPlayerTurnPhaseTwo"
            },
            ["MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet"] = new[] { "_nextId" },
            // `_nextHookId` 是共享确定性计数器；`CombatState` 是“本机队列处在
            // PlayPhase / EndTurnPhaseOne / NotPlayPhase”的本地视图，进快照但不参与共享哈希。
            ["MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer"] =
                new[] { "_nextHookId", "CombatState" },
            ["MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceSynchronizer"] = new[] { "_choiceIds" },
            ["MegaCrit.Sts2.Core.Multiplayer.Game.ChecksumTracker"] = new[] { "NextId" },
            ["MegaCrit.Sts2.Core.Combat.CombatManager"] = new[] { "_playerActionsDisabled" }
        };

    /// <summary>
    /// 字段作用域。默认 Shared；只有明确属于“本机视角”的少数几个字段标 Local。
    /// 新增多人字段时改这张表，不要把 if 散到快照核心。
    /// </summary>
    public static SnapshotScope ScopeOf(FieldInfo field)
    {
        string type = field?.DeclaringType?.FullName ?? string.Empty;
        string name = field?.Name ?? string.Empty;

        if (type == "MegaCrit.Sts2.Core.Combat.CombatManager" &&
            name.IndexOf("_playerActionsDisabled", StringComparison.Ordinal) >= 0)
        {
            return SnapshotScope.Local;
        }

        if (type == "MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSynchronizer" &&
            name.IndexOf("CombatState", StringComparison.Ordinal) >= 0)
        {
            return SnapshotScope.Local;
        }

        return SnapshotScope.Shared;
    }

    private static bool IsRestricted(string typeName)
    {
        return typeName != null && RestrictedFields.ContainsKey(typeName);
    }

    /// <summary>
    /// 纯瞬时字段（入队时置位、结算后由游戏自己清）。存进快照会让"恢复后的立刻状态"和捕获时不一致，
    /// 也把一次动作的中间态固化成节点的一部分。
    /// </summary>
    private static readonly string[] TransientPropertyNames =
    {
        "IsQueued"
    };

    /// <summary>
    /// 卡牌上的“指向中的目标”：拖拽/瞄准时置位，动作一结算就清空。
    /// 它属于输入瞬态而不是可重放状态；存进快照会让客户端在动作收尾那一帧抓到的节点
    /// 带着一个“已经打完了但还指着敌人”的幽灵目标（实测 M2 镜像锚点时会命中）。
    ///
    /// 另外三个是**惰性展示缓存**：
    ///   · `_titleLocString` 是 `TitleLocString` 的 `??=` 缓存；
    ///   · `_starCostSet` / `_baseStarCost` 是 `BaseStarCost` 首次读取时从 canonical 抄过来的缓存。
    /// 战斗房间重建（跳转后必做）会读这些属性，从而在“捕获之后”把 null 变成有值，
    /// 让 `rew_verify` 报出一堆和玩法无关的不一致。它们的真值都由 canonical 模型决定，
    /// 不进快照、也不该参与逐字节比较。
    /// </summary>
    private static readonly string[] CardTransientFields =
    {
        "_currentTarget",
        "_titleLocString",
        "_starCostSet",
        "_baseStarCost"
    };

    private static bool MatchesProperty(FieldInfo field, string[] names)
    {
        string name = field.Name;
        foreach (string candidate in names)
        {
            if (name.IndexOf(candidate, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFieldIncluded(FieldInfo field)
    {
        Type declaring = field.DeclaringType;
        if (declaring == null)
        {
            return true;
        }

        if (RestrictedFields.TryGetValue(declaring.FullName ?? string.Empty, out string[] allowed))
        {
            return MatchesProperty(field, allowed);
        }

        if (declaring.FullName == "MegaCrit.Sts2.Core.Models.PotionModel" && MatchesProperty(field, TransientPropertyNames))
        {
            // 药水的 IsQueued 是"已点但还没结算"的入队标志：动作一结算就由游戏清掉。
            // 它不属于可重放的状态，存进去只会让恢复后的即时状态和捕获时对不上。
            return false;
        }

        if (declaring.FullName == "MegaCrit.Sts2.Core.Models.CardModel"
            && MatchesProperty(field, CardTransientFields))
        {
            return false;
        }

        return true;
    }

    public static bool IsOpaque(Type type, object value)
    {
        if (type == null)
        {
            return true;
        }

        if (type == typeof(string))
        {
            return true;
        }

        if (type.IsPrimitive || type.IsEnum)
        {
            return true;
        }

        if (type == typeof(decimal) || type == typeof(DateTime) || type == typeof(TimeSpan) ||
            type == typeof(Guid) || type == typeof(IntPtr) || type == typeof(UIntPtr))
        {
            return true;
        }

        // Godot 的节点、资源、结构体（Vector2 / Color ...）
        if (typeof(GodotObject).IsAssignableFrom(type))
        {
            return true;
        }

        string ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns) && (ns == "Godot" || ns.StartsWith("Godot.", StringComparison.Ordinal)))
        {
            return true;
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return true;
        }

        if (typeof(Task).IsAssignableFrom(type))
        {
            return true;
        }

        if (typeof(MemberInfo).IsAssignableFrom(type) || type == typeof(Type) || type == typeof(Assembly))
        {
            return true;
        }

        if (typeof(CancellationTokenSource).IsAssignableFrom(type) ||
            typeof(WaitHandle).IsAssignableFrom(type) ||
            typeof(Thread).IsAssignableFrom(type))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(ns) && ns.StartsWith("MegaCrit.Sts2.Core.Logging", StringComparison.Ordinal))
        {
            return true;
        }

        // 编译器生成的迭代器 / async 状态机（名字形如 <AddCustomEncounters>d__1）：
        // 它们是临时对象，字段多是 <>2__current 之类的槽位，写回会抛 NotSupportedException，
        // 而且恢复它们本身没有意义。
        if (!string.IsNullOrEmpty(type.Name) && type.Name[0] == '<')
        {
            return true;
        }

        if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
        {
            return true;
        }

        if (typeof(System.Collections.IEnumerator).IsAssignableFrom(type))
        {
            return true;
        }

        if (IsRestricted(type.FullName))
        {
            // 例外：这些管理器 / 同步器允许遍历，但字段由 IsFieldIncluded 的白名单过滤。
            // 少了它们，官方 replay 的确定性计数器就不在快照里。
            return false;
        }

        // canonical 模型是共享的不可变内容，只保留引用
        if (value is AbstractModel model && model.IsCanonical)
        {
            return true;
        }

        return false;
    }


    /// <summary>标量：按值存下来，不参与对象图遍历。</summary>
    public static bool IsScalar(Type type)
    {
        return type.IsPrimitive
            || type.IsEnum
            || type == typeof(string)
            || type == typeof(decimal)
            || type == typeof(DateTime)
            || type == typeof(TimeSpan)
            || type == typeof(Guid);
    }

    /// <summary>
    /// 禁止写进快照的引用类型：Godot 对象、委托、Task、线程原语、反射元数据、logger。
    /// 这些既不遍历也不写回。
    /// </summary>
    public static bool IsForbidden(Type type)
    {
        if (typeof(GodotObject).IsAssignableFrom(type))
        {
            return true;
        }

        string ns = type.Namespace;
        if (!string.IsNullOrEmpty(ns) && (ns == "Godot" || ns.StartsWith("Godot.", StringComparison.Ordinal)))
        {
            return true;
        }

        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Task).IsAssignableFrom(type))
        {
            return true;
        }

        if (typeof(MemberInfo).IsAssignableFrom(type) || type == typeof(Type) || type == typeof(Assembly))
        {
            return true;
        }

        if (typeof(CancellationTokenSource).IsAssignableFrom(type) ||
            typeof(WaitHandle).IsAssignableFrom(type) ||
            typeof(Thread).IsAssignableFrom(type))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(ns) && ns.StartsWith("MegaCrit.Sts2.Core.Logging", StringComparison.Ordinal))
        {
            return true;
        }

        if (IsRestricted(type.FullName))
        {
            // 同上：类型本身可以遍历，能不能写由字段白名单决定。
            return false;
        }

        if (!string.IsNullOrEmpty(type.Name) && type.Name[0] == '<')
        {
            return true;
        }

        if (type.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false))
        {
            return true;
        }

        return typeof(System.Collections.IEnumerator).IsAssignableFrom(type);
    }

    public static FieldInfo[] FieldsOf(Type type)
    {
        return FieldCache.GetOrAdd(type, static t =>
        {
            var list = new System.Collections.Generic.List<FieldInfo>(32);
            for (Type current = t; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo[] fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                foreach (FieldInfo field in fields)
                {
                    if (field.IsStatic || field.IsLiteral)
                    {
                        continue;
                    }

                    if (field.FieldType.IsPointer || field.FieldType.IsByRefLike)
                    {
                        continue;
                    }

                    // 只过滤"不能存"的类型（Godot / 委托 / Task ...）。
                    // 注意不能用 IsOpaque：它会把 int/enum/string 也过滤掉，
                    // 导致 energy / phase / HP 这类标量字段整个丢失。
                    if (IsForbidden(field.FieldType))
                    {
                        continue;
                    }

                    if (!IsFieldIncluded(field))
                    {
                        continue;
                    }

                    list.Add(field);
                }
            }

            return list.ToArray();
        });
    }
}
