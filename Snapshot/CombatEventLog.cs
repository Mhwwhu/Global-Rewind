using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 本场战斗的完整事件流水账。
///
/// 回溯树只记录"静止决策点"，但"跳转到节点等价于从战斗开始按操作树重放到该节点"
/// 要求能证明中途没有任何事件被吞掉。这里按发生顺序记录：
///   · 每个 GameAction 入队（含 hook —— 消耗 / 变牌 / 弃牌 / 保留手牌的载体都走 hook）
///   · 动作完成 / 取消 / 玩家选择后恢复
///   · 每次玩家选择（PlayerChoiceReceived）
///   · 校验和生成、回合开始、抓点、跳转、剪枝
///
/// 事件只在游戏线程追加，MCP 工具经 ProcessFrame 主线程读取，用锁兜住跨线程写入。
/// </summary>
internal sealed class CombatEventLog
{
    private const int MaxEntries = 4096;

    private sealed class Entry
    {
        public int Seq;
        public string Kind;
        public string Detail;
        public string Type;
        public uint ActionId;
        public int Round;
        public int Turn;
        public int NodeId;
        public ulong ActorId;
        public string Stream;
        public uint LogicalId;
        public string Frontier;
    }

    private readonly object _gate = new object();
    private readonly List<Entry> _entries = new List<Entry>(512);
    private int _seq;
    private bool _truncated;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public bool Truncated
    {
        get
        {
            lock (_gate)
            {
                return _truncated;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _seq = 0;
            _truncated = false;
        }
    }

    public void Add(string kind, string type, string detail, uint actionId = 0,
                    int round = 0, int turn = 0, int nodeId = 0,
                    ulong actorId = 0, string stream = null, uint logicalId = 0,
                    string frontier = null)
    {
        lock (_gate)
        {
            if (_entries.Count >= MaxEntries)
            {
                _truncated = true;
                return;
            }

            _entries.Add(new Entry
            {
                Seq = ++_seq,
                Kind = kind ?? "?",
                Type = type ?? "?",
                Detail = detail ?? string.Empty,
                ActionId = actionId,
                Round = round,
                Turn = turn,
                NodeId = nodeId,
                ActorId = actorId,
                Stream = stream ?? "system",
                LogicalId = logicalId,
                Frontier = frontier
            });
        }
    }

    /// <summary>
    /// 导出为 JSON。last > 0 时只返回最后 N 条；kind 非空时先按 kind 过滤。
    /// total 是累计条数，matched 是过滤后条数，returned 是本次实际返回条数。
    /// </summary>
    public string ToJson(int last = 0, string kind = null, ulong actorId = 0, string stream = null)
    {
        List<Entry> copy;
        bool truncated;
        int total;
        lock (_gate)
        {
            total = _entries.Count;
            truncated = _truncated;
            copy = new List<Entry>(_entries);
        }

        IEnumerable<Entry> selected = copy;
        if (!string.IsNullOrEmpty(kind))
        {
            selected = selected.Where(e => string.Equals(e.Kind, kind, StringComparison.Ordinal));
        }

        if (!string.IsNullOrEmpty(stream))
        {
            selected = selected.Where(e => string.Equals(e.Stream, stream, StringComparison.Ordinal));
        }

        if (actorId != 0)
        {
            selected = selected.Where(e => e.ActorId == actorId);
        }

        List<Entry> list = selected.ToList();
        int matched = list.Count;
        if (last > 0 && list.Count > last)
        {
            list = list.GetRange(list.Count - last, last);
        }

        var sb = new StringBuilder(list.Count * 96 + 128);
        sb.Append("{\"total\":").Append(total.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"matched\":").Append(matched.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"returned\":").Append(list.Count.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"truncated\":").Append(truncated ? "true" : "false");
        sb.Append(",\"events\":[");
        for (int i = 0; i < list.Count; i++)
        {
            Entry e = list[i];
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"seq\":").Append(e.Seq.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"kind\":").Append(JsonSerializer.Serialize(e.Kind));
            sb.Append(",\"type\":").Append(JsonSerializer.Serialize(e.Type));
            sb.Append(",\"detail\":").Append(JsonSerializer.Serialize(e.Detail));
            sb.Append(",\"action_id\":").Append(e.ActionId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"round\":").Append(e.Round.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"turn\":").Append(e.Turn.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"node_id\":").Append(e.NodeId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"actor_id\":").Append(e.ActorId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"stream\":").Append(JsonSerializer.Serialize(e.Stream));
            sb.Append(",\"logical_id\":").Append(e.LogicalId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"frontier\":").Append(JsonSerializer.Serialize(e.Frontier));
            sb.Append('}');
        }

        sb.Append("]}");
        return sb.ToString();
    }
}
