using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;

namespace GlobalRewind.Players;

/// <summary>
/// `CombatTurnState` 是 internal 类型，多人流程状态（谁已结束回合、额外回合名单）
/// 只能通过反射读。这里集中做反射与缓存，避免每个调用点各写一遍。
///
/// 只读数据字段；`ReadyLock` / `EndTurnSignalSource` / `BeginEnemyTurnSignalSource` 一概不碰。
/// </summary>
internal static class TurnStateProbe
{
    private static readonly Dictionary<string, PropertyInfo> PropertyCache =
        new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

    private static readonly Dictionary<string, FieldInfo> FieldCache =
        new Dictionary<string, FieldInfo>(StringComparer.Ordinal);

    public static object GetTurnState()
    {
        try
        {
            CombatManager manager = CombatManager.Instance;
            if (manager == null)
            {
                return null;
            }

            FieldInfo field = typeof(CombatManager).GetField(
                "_turnState", BindingFlags.Instance | BindingFlags.NonPublic);

            return field?.GetValue(manager);
        }
        catch
        {
            return null;
        }
    }

    public static bool ReadBool(string propertyName)
    {
        object turnState = GetTurnState();
        if (turnState == null)
        {
            return false;
        }

        try
        {
            PropertyInfo property = ResolveProperty(turnState.GetType(), propertyName);
            return property?.GetValue(turnState) is bool value && value;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<Player> ReadPlayers(string propertyName)
    {
        var players = new List<Player>(4);

        object turnState = GetTurnState();
        if (turnState == null)
        {
            return players;
        }

        try
        {
            PropertyInfo property = ResolveProperty(turnState.GetType(), propertyName);
            if (property?.GetValue(turnState) is not IEnumerable items)
            {
                return players;
            }

            foreach (object item in items)
            {
                if (item is Player player)
                {
                    players.Add(player);
                }
            }
        }
        catch
        {
            // 读不到就当空集合：调用方会退回“全体存活玩家”。
        }

        return players;
    }

    /// <summary>读某个对象上的私有集合字段的元素数（`_receivedChoices` / `_hookActions` ...）。</summary>
    public static int CountCollection(object target, string fieldName)
    {
        if (target == null)
        {
            return 0;
        }

        try
        {
            FieldInfo field = ResolveField(target.GetType(), fieldName);
            object value = field?.GetValue(target);

            if (value is ICollection collection)
            {
                return collection.Count;
            }

            if (value is IEnumerable enumerable)
            {
                int count = 0;
                foreach (object _ in enumerable)
                {
                    count++;
                }

                return count;
            }
        }
        catch
        {
            // 读不到按 0 处理；真正的“非空”会在别的判据里暴露。
        }

        return 0;
    }

    /// <summary>
    /// 清空某个对象上的私有集合字段（`_receivedChoices` / `_hookActions` /
    /// `_requestedActionsWaitingForPlayerTurn`）。这些都是“在途传输态”，不进快照，
    /// 写回之后必须显式清空，否则旧 choice/hook 会命中过期的等待者。
    /// </summary>
    public static bool ClearCollection(object target, string fieldName)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            FieldInfo field = ResolveField(target.GetType(), fieldName);
            object value = field?.GetValue(target);

            switch (value)
            {
                case IList list:
                    list.Clear();
                    return true;
                case IDictionary dictionary:
                    dictionary.Clear();
                    return true;
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static PropertyInfo ResolveProperty(Type type, string name)
    {
        lock (PropertyCache)
        {
            if (PropertyCache.TryGetValue(name, out PropertyInfo cached) && cached.DeclaringType == type)
            {
                return cached;
            }

            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null)
            {
                PropertyCache[name] = property;
            }

            return property;
        }
    }

    private static FieldInfo ResolveField(Type type, string name)
    {
        lock (FieldCache)
        {
            if (FieldCache.TryGetValue(name, out FieldInfo cached) && cached.DeclaringType == type)
            {
                return cached;
            }

            FieldInfo field = type.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null)
            {
                FieldCache[name] = field;
            }

            return field;
        }
    }
}
