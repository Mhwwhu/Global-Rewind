using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using GlobalRewind.Logging;

namespace GlobalRewind.Snapshot;

/// <summary>
/// 一次战斗状态的"原地 memento"。
///
/// 做法不是手写字段清单，而是从若干根对象出发遍历整张对象图：
///   · 每个对象的每个实例字段都记下（值类型记值，引用类型记引用）；
///   · 集合（数组 / IList / IDictionary / HashSet）额外把元素顺序整份拷下来；
///   · 恢复时写回"同一批实例"，因此 UI 节点上的事件订阅、模型之间的引用关系都不会断。
///
/// 这也意味着快照必须保持强引用：被撤销掉的卡牌 / 生物对象要靠快照活着才能被写回去。
/// </summary>
internal sealed class ObjectGraphSnapshot
{
    private const int MaxObjects = 40000;
    private const int MaxCollectionItems = 65536;

    private readonly List<ObjectRecord> _records;

    private ObjectGraphSnapshot(List<ObjectRecord> records, bool truncated)
    {
        _records = records;
        Truncated = truncated;
    }

    /// <summary>对象数超过上限，图只记了一部分。此时 Verify 覆盖不到的部分不受任何保证。</summary>
    public bool Truncated { get; }

    public int ObjectCount => _records.Count;

    public int FieldCount
    {
        get
        {
            int total = 0;
            foreach (ObjectRecord record in _records)
            {
                total += record.Fields.Count;
            }

            return total;
        }
    }

    public int SharedObjectCount
    {
        get
        {
            int total = 0;
            foreach (ObjectRecord record in _records)
            {
                if (record.Scope == SnapshotScope.Shared)
                {
                    total++;
                }
            }

            return total;
        }
    }

    public int SharedFieldCount
    {
        get
        {
            int total = 0;
            foreach (ObjectRecord record in _records)
            {
                if (record.Scope != SnapshotScope.Shared)
                {
                    continue;
                }

                foreach (FieldSlot slot in record.Fields)
                {
                    if (slot.Scope == SnapshotScope.Shared)
                    {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    public static ObjectGraphSnapshot Capture(params SnapshotRoot[] roots)
    {
        var builder = new Builder();
        if (roots != null)
        {
            foreach (SnapshotRoot root in roots)
            {
                builder.Visit(root.Value, root.Scope);
            }
        }

        return new ObjectGraphSnapshot(builder.Build(), builder.Truncated);
    }

    /// <summary>
    /// 把快照写回原实例。返回未能写回的字段数（正常应为 0）。
    ///
    /// 逆序写回（深度递减）是必需的：结构体字段是按装箱后的"盒子"记录的，
    /// 先恢复子记录（盒子内部），再让父记录把这个盒子 SetValue 回字段，才能把新值拷进字段；
    /// 正序会让父记录先把旧盒子拷进字段，子记录随后改的只是那个孤立的盒子。
    /// </summary>
    public int Restore()
    {
        int failures = 0;

        for (int i = _records.Count - 1; i >= 0; i--)
        {
            ObjectRecord record = _records[i];
            foreach (FieldSlot slot in record.Fields)
            {
                try
                {
                    if (slot.Collection != null)
                    {
                        slot.Collection.RestoreContents();
                        if (slot.WriteReference)
                        {
                            slot.Field.SetValue(record.Target, slot.Collection.Target);
                        }
                    }
                    else if (slot.WriteReference)
                    {
                        slot.Field.SetValue(record.Target, slot.Value);
                    }
                }
                catch (Exception e)
                {
                    failures++;
                    ModLog.Warn($"restore field {record.Target.GetType().Name}.{slot.Field.Name} failed: {e.GetType().Name} {e.Message}");
                }
            }
        }

        return failures;
    }

    /// <summary>
    /// 逐字段核对"快照里的值"和"现在对象上的值"，用于证明恢复是完整的。
    ///
    /// Restore() 只能报告"写失败"，不能发现"写了但没落到目标上"（结构体装箱、
    /// 只读集合内容没回去、SetValue 被属性 setter 规范化掉等）。这里重新读一遍，
    /// 按捕获时的比较方式（引用比引用、标量比值、集合比内容）逐个核对。
    ///
    /// 只在刚跳转完时调用才有意义：之后游戏继续推进，字段自然会变。
    /// </summary>
    public VerifyReport Verify(HashScope scope = HashScope.Full)
    {
        var report = new VerifyReport { Records = _records.Count, Truncated = Truncated };

        foreach (ObjectRecord record in _records)
        {
            if (scope == HashScope.Shared && record.Scope != SnapshotScope.Shared)
            {
                continue;
            }

            foreach (FieldSlot slot in record.Fields)
            {
                if (scope == HashScope.Shared && slot.Scope != SnapshotScope.Shared)
                {
                    continue;
                }

                // 只读的非集合字段本来就不写回，也无从变化，不参与核对
                if (slot.Collection == null && !slot.WriteReference)
                {
                    continue;
                }

                report.Fields++;

                object current;
                try
                {
                    current = slot.Field.GetValue(record.Target);
                }
                catch (Exception e)
                {
                    report.Add($"{Name(record.Target)}.{slot.Field.Name}: read failed ({e.GetType().Name})");
                    continue;
                }

                if (slot.Collection != null)
                {
                    if (!ReferenceEquals(current, slot.Collection.Target))
                    {
                        report.Add($"{Name(record.Target)}.{slot.Field.Name}: collection instance replaced");
                        continue;
                    }

                    if (!slot.Collection.ContentsEqual(out string detail))
                    {
                        report.Add($"{Name(record.Target)}.{slot.Field.Name}: {detail}");
                    }

                    continue;
                }

                if (!SameAsCaptured(current, slot.Value))
                {
                    report.Add($"{Name(record.Target)}.{slot.Field.Name}: {Describe(current)} != {Describe(slot.Value)}");
                }
            }
        }

        return report;
    }

    private static string Name(object target)
    {
        return target == null ? "<null>" : target.GetType().Name;
    }

    private static string Describe(object value)
    {
        if (value == null)
        {
            return "null";
        }

        string text = value is string s ? s : value.ToString();
        if (text != null && text.Length > 40)
        {
            text = text.Substring(0, 40) + "...";
        }

        return $"{value.GetType().Name}({text})";
    }

    /// <summary>
    /// 捕获时是怎么存的，就怎么比：值类型/字符串比值，引用类型比引用。
    /// （标量字段存的是装箱值，不能比引用。）
    /// </summary>
    private static bool SameAsCaptured(object current, object captured)
    {
        if (ReferenceEquals(current, captured))
        {
            return true;
        }

        if (current == null || captured == null)
        {
            return false;
        }

        Type type = captured.GetType();
        if (type.IsValueType || captured is string)
        {
            return current.GetType() == type && Equals(current, captured);
        }

        return false;
    }

    /// <summary>
    /// 捕获时的"规范序列化"哈希：把整张对象图按固定路径、固定字段顺序、固定取值编码写成一串文本再取 SHA256。
    ///
    /// 这是"深拷贝序列化结果逐字节一致"的判定依据：同一个状态序列化两次字节完全相同，
    /// 状态只要差一个比特（含浮点位、集合顺序、引用指向）哈希就不同。
    /// </summary>
    public string DeepHash => _deepHash ??= ComputeHash(HashScope.Full);

    private string _deepHash;

    /// <summary>
    /// 只包含共享字段的规范哈希。
    /// 注意：它仍然是**本进程内**的指纹（引用编码用记录序号），只用于
    /// “同进程捕获态 vs 活态”和“同一端的兄弟节点合并”；
    /// 跨端比较请用游戏的 `NetFullCombatState` 纯哈希（<see cref="CombatChecksum"/>）。
    /// </summary>
    public string SharedDeepHash => _sharedDeepHash ??= ComputeHash(HashScope.Shared);

    private string _sharedDeepHash;

    /// <summary>
    /// 合并用哈希：忽略 `ActionQueueSet._nextId`（已分配、但可能还没执行的动作编号）。
    /// 只影响“是否并入同一个节点”，不影响节点内容与跳转校验。
    /// </summary>
    public string MergeHash => _mergeHash ??= ComputeHash(HashScope.Merge);

    private string _mergeHash;

    public string SharedMergeHash => _sharedMergeHash ??= ComputeHash(HashScope.SharedMerge);

    private string _sharedMergeHash;

    /// <summary>
    /// 把"快照里记的"和"现在对象上的"各做一次规范序列化，逐字节比较。
    /// 和 <see cref="Verify"/> 的区别：这条路径会重新读取活对象、重新枚举活集合，
    /// 是"深拷贝两份再对比"，而不是"回读自己记下的盒子"。
    /// </summary>
    public DeepCompareResult DeepCompare(HashScope scope = HashScope.Full)
    {
        var result = new DeepCompareResult();
        try
        {
            string captured = BuildCanonical(fromLive: false, scope);
            string live = BuildCanonical(fromLive: true, scope);
            result.CapturedHash = Hash(captured);
            result.LiveHash = Hash(live);
            result.Equal = string.Equals(captured, live, StringComparison.Ordinal);
            if (!result.Equal)
            {
                result.Diff = FirstDifference(captured, live);
            }
        }
        catch (Exception e)
        {
            result.Equal = false;
            result.Diff = $"{e.GetType().Name}: {e.Message}";
        }

        return result;
    }

    /// <summary>诊断用：导出某个口径的规范序列化文本（供两个节点逐行对比）。</summary>
    internal string DebugCanonical(HashScope scope)
    {
        return BuildCanonical(fromLive: false, scope);
    }

    /// <summary>诊断用：按类型统计对象图里的记录数（对比两个节点的结构差异）。</summary>
    internal Dictionary<string, int> DebugRecordCensus()
    {
        var census = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ObjectRecord record in _records)
        {
            string type = TypeName(record.Target);
            census[type] = census.TryGetValue(type, out int count) ? count + 1 : 1;
        }

        return census;
    }

    private string ComputeHash(HashScope scope)
    {
        try
        {
            return Hash(BuildCanonical(fromLive: false, scope));
        }
        catch (Exception e)
        {
            ModLog.Warn($"deep hash failed ({scope}): {e.GetType().Name} {e.Message}");
            return null;
        }
    }

    private static string Hash(string text)
    {
        using (var sha = SHA256.Create())
        {
            byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var sb = new StringBuilder(32);
            for (int i = 0; i < 16; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }

    private static string FirstDifference(string captured, string live)
    {
        string[] a = captured.Split('\n');
        string[] b = live.Split('\n');
        int count = Math.Min(a.Length, b.Length);
        var diffs = new List<string>(6);

        for (int i = 0; i < count && diffs.Count < 6; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
            {
                diffs.Add($"line {i + 1}\n  captured: {a[i]}\n  live    : {b[i]}");
            }
        }

        if (diffs.Count == 0 && a.Length != b.Length)
        {
            diffs.Add($"line count {a.Length} != {b.Length}");
        }

        return string.Join("\n", diffs);
    }

    /// <summary>
    /// 规范序列化。格式刻意做成"一行一个字段"，便于比对时直接指出第几行、期望什么、实际什么。
    ///   r{index}|{type}
    ///   f|{declaringType}|{field}|{value}
    /// 取值编码：
    ///   null / b:0 / i:12 / f:{float bits} / d:{double bits} / m:{decimal} / s:{escaped}
    ///   e:{enumType}.{value} / #{recordIndex}（指向图内对象） / ~{type}#{instance}（图外对象）
    ///   [{kind}#{count}]...（集合，逐元素编码）
    /// </summary>
    private string BuildCanonical(bool fromLive, HashScope scope)
    {
        bool sharedOnly = scope is HashScope.Shared or HashScope.SharedMerge;
        bool merge = scope is HashScope.Merge or HashScope.SharedMerge;

        var index = new Dictionary<object, int>(_records.Count, ReferenceEqualityComparer.Instance);
        for (int i = 0; i < _records.Count; i++)
        {
            index[_records[i].Target] = i;
        }

        var sb = new StringBuilder(_records.Count * 96);
        for (int i = 0; i < _records.Count; i++)
        {
            ObjectRecord record = _records[i];
            if (sharedOnly && record.Scope != SnapshotScope.Shared)
            {
                continue;
            }

            sb.Append('r').Append(i.ToString(CultureInfo.InvariantCulture))
              .Append('|').Append(TypeName(record.Target)).Append('\n');

            foreach (FieldSlot slot in record.Fields)
            {
                if (sharedOnly && slot.Scope != SnapshotScope.Shared)
                {
                    continue;
                }

                if (merge && IsQueuedActionCounter(slot))
                {
                    continue;
                }

                sb.Append("f|")
                  .Append(slot.Field.DeclaringType?.FullName ?? "?")
                  .Append('|').Append(slot.Field.Name)
                  .Append('|');

                if (slot.Collection != null)
                {
                    if (fromLive)
                    {
                        object liveValue;
                        try
                        {
                            liveValue = slot.Field.GetValue(record.Target);
                        }
                        catch (Exception e)
                        {
                            sb.Append("!read:").Append(e.GetType().Name).Append('\n');
                            continue;
                        }

                        if (!ReferenceEquals(liveValue, slot.Collection.Target))
                        {
                            // 集合实例被换掉了：捕获时的那个已经不是字段当前指向的对象
                            sb.Append("!replaced:").Append(TypeName(liveValue)).Append('\n');
                            continue;
                        }
                    }

                    AppendCollection(sb, slot.Collection, index, fromLive);
                }
                else
                {
                    object value = slot.Value;
                    if (fromLive)
                    {
                        try
                        {
                            value = slot.Field.GetValue(record.Target);
                        }
                        catch (Exception e)
                        {
                            sb.Append("!read:").Append(e.GetType().Name);
                            sb.Append('\n');
                            continue;
                        }
                    }

                    AppendValue(sb, value, index);
                }

                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    private static string TypeName(object target)
    {
        return target == null ? "<null>" : target.GetType().FullName;
    }

    /// <summary>
    /// `ActionQueueSet._nextId`：动作在**入队时**就分配编号，所以快速连点时
    /// “A 结算完”的快照里已经包含排队中的 B 的编号。模型状态相同、只有这个计数器不同，
    /// 合并时应当视为同一个节点。
    /// </summary>
    private static bool IsQueuedActionCounter(FieldSlot slot)
    {
        return string.Equals(slot.Field.Name, "_nextId", StringComparison.Ordinal)
               && string.Equals(slot.Field.DeclaringType?.FullName,
                   "MegaCrit.Sts2.Core.GameActions.Multiplayer.ActionQueueSet", StringComparison.Ordinal);
    }

    private static void AppendValue(StringBuilder sb, object value, Dictionary<object, int> index)
    {
        if (value == null)
        {
            sb.Append("null");
            return;
        }

        if (value is string text)
        {
            sb.Append("s:").Append(Escape(text));
            return;
        }

        Type type = value.GetType();
        if (type.IsEnum)
        {
            sb.Append('e').Append(':').Append(type.FullName).Append('.')
              .Append(Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
            return;
        }

        switch (value)
        {
            case bool b:
                sb.Append(b ? "b:1" : "b:0");
                return;
            case char c:
                sb.Append("c:").Append(((int)c).ToString(CultureInfo.InvariantCulture));
                return;
            case float f:
                sb.Append("f:").Append(BitConverter.SingleToInt32Bits(f).ToString("x8", CultureInfo.InvariantCulture));
                return;
            case double d:
                sb.Append("d:").Append(BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture));
                return;
            case decimal m:
                sb.Append("m:").Append(m.ToString(CultureInfo.InvariantCulture));
                return;
            case DateTime dt:
                sb.Append("t:").Append(dt.ToString("O", CultureInfo.InvariantCulture));
                return;
            case TimeSpan ts:
                sb.Append("ts:").Append(ts.Ticks.ToString(CultureInfo.InvariantCulture));
                return;
            case Guid g:
                sb.Append("g:").Append(g.ToString("N"));
                return;
            case IntPtr ip:
                sb.Append("p:").Append(ip.ToInt64().ToString(CultureInfo.InvariantCulture));
                return;
            case UIntPtr up:
                sb.Append("up:").Append(up.ToUInt64().ToString(CultureInfo.InvariantCulture));
                return;
            case sbyte or byte or short or ushort or int or uint or long or ulong:
                sb.Append("i:").Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
        }

        // 值类型一律按内容编码：读字段时永远会新建一个装箱副本，
        // 拿实例标识去比会把"内容完全相同"误判成不一致。
        if (type.IsValueType)
        {
            AppendStructContent(sb, value, type, index, 0);
            return;
        }

        if (index.TryGetValue(value, out int recordIndex))
        {
            sb.Append('#').Append(recordIndex.ToString(CultureInfo.InvariantCulture));
            return;
        }

        // 图外对象（canonical 模型、被显式排除的类型）：只能靠类型 + 实例标识区分。
        // 实例在恢复过程中保持不变（memento 写回同一批对象），所以这个标识在两次序列化之间是稳定的。
        sb.Append('~').Append(type.FullName).Append('#')
          .Append(RuntimeHelpers.GetHashCode(value).ToString("x8", CultureInfo.InvariantCulture));
    }

    private static void AppendStructContent(StringBuilder sb, object value, Type type, Dictionary<object, int> index, int depth)
    {
        if (depth >= 8)
        {
            sb.Append("<deep>");
            return;
        }

        sb.Append('{').Append(type.FullName);
        foreach (FieldInfo field in MementoRules.FieldsOf(type))
        {
            sb.Append('|').Append(field.Name).Append('=');
            object inner;
            try
            {
                inner = field.GetValue(value);
            }
            catch (Exception e)
            {
                sb.Append('!').Append(e.GetType().Name);
                continue;
            }

            AppendValue(sb, inner, index);
        }

        sb.Append('}');
    }

    private static void AppendCollection(StringBuilder sb, CollectionCopy collection, Dictionary<object, int> index, bool fromLive)
    {
        CollectionKind kind = collection.Kind;
        sb.Append('[').Append(kind).Append('#');

        switch (kind)
        {
            case CollectionKind.Dictionary:
            {
                var entries = new List<string>();
                if (fromLive)
                {
                    if (collection.Target is IDictionary live)
                    {
                        foreach (DictionaryEntry entry in live)
                        {
                            entries.Add(EncodeValue(entry.Key, index) + "=>" + EncodeValue(entry.Value, index));
                        }
                    }
                }
                else
                {
                    for (int i = 0; i < collection.Keys.Length; i++)
                    {
                        entries.Add(EncodeValue(collection.Keys[i], index) + "=>" + EncodeValue(collection.Values[i], index));
                    }
                }

                // 字典的枚举顺序在删改之后不可靠，按编码排序保证同一个内容永远同一串
                entries.Sort(StringComparer.Ordinal);
                sb.Append(entries.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                foreach (string entry in entries)
                {
                    sb.Append(entry).Append(';');
                }

                return;
            }

            case CollectionKind.Set:
            {
                var items = new List<string>();
                if (fromLive)
                {
                    if (collection.Target is IEnumerable live)
                    {
                        foreach (object item in live)
                        {
                            items.Add(EncodeValue(item, index));
                            if (items.Count > MaxCollectionItems)
                            {
                                break;
                            }
                        }
                    }
                }
                else
                {
                    foreach (object item in collection.Items)
                    {
                        items.Add(EncodeValue(item, index));
                    }
                }

                // 集合本身无序：排序后再比，避免"内容一样、枚举顺序不同"被当成不一致
                items.Sort(StringComparer.Ordinal);
                sb.Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                foreach (string item in items)
                {
                    sb.Append(item).Append(';');
                }

                return;
            }

            default:
            {
                var items = new List<string>();
                if (fromLive)
                {
                    switch (kind)
                    {
                        case CollectionKind.Array when collection.Target is Array array:
                            for (int i = 0; i < array.Length; i++)
                            {
                                items.Add(EncodeValue(array.GetValue(i), index));
                            }

                            break;
                        case CollectionKind.List when collection.Target is IList list:
                            for (int i = 0; i < list.Count; i++)
                            {
                                items.Add(EncodeValue(list[i], index));
                            }

                            break;
                    }
                }
                else
                {
                    foreach (object item in collection.Items)
                    {
                        items.Add(EncodeValue(item, index));
                    }
                }

                sb.Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                foreach (string item in items)
                {
                    sb.Append(item).Append(';');
                }

                return;
            }
        }
    }

    private static string EncodeValue(object value, Dictionary<object, int> index)
    {
        var sb = new StringBuilder(32);
        AppendValue(sb, value, index);
        return sb.ToString();
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '|': sb.Append("\\p"); break;
                case ';': sb.Append("\\s"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    private sealed class ObjectRecord
    {
        public object Target;
        public SnapshotScope Scope;
        public readonly List<FieldSlot> Fields = new List<FieldSlot>(32);
    }

    private struct FieldSlot
    {
        public FieldInfo Field;
        public object Value;
        public CollectionCopy Collection;
        public bool WriteReference;
        public SnapshotScope Scope;
    }

    private sealed class Builder
    {
        private readonly List<ObjectRecord> _records = new List<ObjectRecord>(2048);
        private readonly Dictionary<object, ObjectRecord> _recordsByObject =
            new Dictionary<object, ObjectRecord>(2048, ReferenceEqualityComparer.Instance);
        private readonly Queue<(object Value, SnapshotScope Scope)> _pending =
            new Queue<(object Value, SnapshotScope Scope)>(2048);
        private bool _truncated;

        public bool Truncated => _truncated;

        public void Visit(object value, SnapshotScope scope)
        {
            if (value == null)
            {
                return;
            }

            if (_recordsByObject.TryGetValue(value, out ObjectRecord existing))
            {
                // 共享根先访问：如果同一个对象已经从本地路径进来过，提升为共享。
                if (existing.Scope == SnapshotScope.Local && scope == SnapshotScope.Shared)
                {
                    existing.Scope = SnapshotScope.Shared;
                }

                return;
            }

            if (_recordsByObject.Count >= MaxObjects)
            {
                // 静默截断会让"恢复完整"变成一句空话：少记的对象根本不会出现在 Verify 里
                _truncated = true;
                return;
            }

            Type type = value.GetType();
            if (MementoRules.IsOpaque(type, value))
            {
                return;
            }

            var record = new ObjectRecord { Target = value, Scope = scope };
            _recordsByObject[value] = record;
            _pending.Enqueue((value, scope));
        }

        public List<ObjectRecord> Build()
        {
            while (_pending.Count > 0)
            {
                (object target, SnapshotScope recordScope) = _pending.Dequeue();
                ObjectRecord record = _recordsByObject[target];

                foreach (FieldInfo field in MementoRules.FieldsOf(target.GetType()))
                {
                    SnapshotScope fieldScope = MementoRules.ScopeOf(field);
                    if (fieldScope == SnapshotScope.Excluded)
                    {
                        continue;
                    }

                    object value;
                    try
                    {
                        value = field.GetValue(target);
                    }
                    catch
                    {
                        continue;
                    }

                    var slot = new FieldSlot
                    {
                        Field = field,
                        // readonly 字段（大量集合字段都是）只恢复内容，不写引用
                        WriteReference = !field.IsInitOnly,
                        Scope = fieldScope
                    };

                    if (value == null)
                    {
                        slot.Value = null;
                        record.Fields.Add(slot);
                        continue;
                    }

                    // Godot 对象 / 委托 / Task 一律不存
                    if (MementoRules.IsForbidden(value.GetType()))
                    {
                        continue;
                    }

                    // 标量（int / enum / string / bool ...）按值存，不遍历。
                    // 这一步以前被 IsOpaque 误过滤，导致 energy / phase / HP 全部丢失。
                    if (MementoRules.IsScalar(value.GetType()))
                    {
                        slot.Value = value;
                        record.Fields.Add(slot);
                        continue;
                    }

                    // 本地字段引用的对象也按本地处理；共享字段沿用当前记录的作用域。
                    SnapshotScope childScope = fieldScope == SnapshotScope.Local
                        ? SnapshotScope.Local
                        : recordScope;

                    CollectionCopy collection = CollectionCopy.TryCreate(value);
                    if (collection != null)
                    {
                        slot.Collection = collection;
                        foreach (object item in collection.ReferencedItems)
                        {
                            Visit(item, childScope);
                        }
                    }
                    else
                    {
                        slot.Value = value;
                        Visit(value, childScope);
                    }

                    record.Fields.Add(slot);
                }

                _records.Add(record);
            }

            if (_truncated)
            {
                ModLog.Warn($"snapshot truncated at {MaxObjects} objects: the rest of the graph is NOT captured "
                            + $"(captured {_records.Count} objects)");
            }

            return _records;
        }
    }

    private enum CollectionKind
    {
        Array,
        List,
        Dictionary,
        Set
    }

    private sealed class CollectionCopy
    {
        private static readonly Dictionary<Type, MethodInfo> ClearCache = new Dictionary<Type, MethodInfo>();
        private static readonly Dictionary<Type, MethodInfo> AddCache = new Dictionary<Type, MethodInfo>();

        public object Target;
        public CollectionKind Kind;
        public object[] Items;
        public object[] Keys;
        public object[] Values;

        public IEnumerable<object> ReferencedItems
        {
            get
            {
                if (Keys != null)
                {
                    foreach (object key in Keys)
                    {
                        yield return key;
                    }
                }

                if (Values != null)
                {
                    foreach (object value in Values)
                    {
                        yield return value;
                    }
                }

                if (Items != null)
                {
                    foreach (object item in Items)
                    {
                        yield return item;
                    }
                }
            }
        }

        public static CollectionCopy TryCreate(object value)
        {
            if (value is Array array)
            {
                if (array.Rank != 1 || array.Length > MaxCollectionItems)
                {
                    return null;
                }

                var items = new object[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    items[i] = array.GetValue(i);
                }

                return new CollectionCopy { Target = array, Kind = CollectionKind.Array, Items = items };
            }

            if (value is IDictionary dictionary)
            {
                if (dictionary.Count > MaxCollectionItems)
                {
                    return null;
                }

                var keys = new object[dictionary.Count];
                var values = new object[dictionary.Count];
                int index = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    keys[index] = entry.Key;
                    values[index] = entry.Value;
                    index++;
                }

                return new CollectionCopy
                {
                    Target = dictionary,
                    Kind = CollectionKind.Dictionary,
                    Keys = keys,
                    Values = values
                };
            }

            if (value is IList list)
            {
                if (list.Count > MaxCollectionItems)
                {
                    return null;
                }

                var items = new object[list.Count];
                list.CopyTo(items, 0);
                return new CollectionCopy { Target = list, Kind = CollectionKind.List, Items = items };
            }

            if (value is IEnumerable && LooksLikeMutableSet(value.GetType()))
            {
                var items = new List<object>();
                foreach (object item in (IEnumerable)value)
                {
                    items.Add(item);
                    if (items.Count > MaxCollectionItems)
                    {
                        return null;
                    }
                }

                return new CollectionCopy { Target = value, Kind = CollectionKind.Set, Items = items.ToArray() };
            }

            return null;
        }

        private static bool LooksLikeMutableSet(Type type)
        {
            MethodInfo clear = type.GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes, null);
            MethodInfo add = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.Name == "Add" && method.GetParameters().Length == 1)
                {
                    add = method;
                    break;
                }
            }

            if (clear == null || add == null)
            {
                return false;
            }

            lock (ClearCache)
            {
                ClearCache[type] = clear;
                AddCache[type] = add;
            }

            return true;
        }

        public void RestoreContents()
        {
            switch (Kind)
            {
                case CollectionKind.Array:
                {
                    var array = (Array)Target;
                    int count = Math.Min(array.Length, Items.Length);
                    for (int i = 0; i < count; i++)
                    {
                        array.SetValue(Items[i], i);
                    }

                    break;
                }

                case CollectionKind.List:
                {
                    var list = (IList)Target;
                    list.Clear();
                    foreach (object item in Items)
                    {
                        list.Add(item);
                    }

                    break;
                }

                case CollectionKind.Dictionary:
                {
                    var dictionary = (IDictionary)Target;
                    dictionary.Clear();
                    for (int i = 0; i < Keys.Length; i++)
                    {
                        dictionary[Keys[i]] = Values[i];
                    }

                    break;
                }

                case CollectionKind.Set:
                {
                    Type type = Target.GetType();
                    ClearCache.TryGetValue(type, out MethodInfo clear);
                    AddCache.TryGetValue(type, out MethodInfo add);
                    clear?.Invoke(Target, null);
                    if (add == null)
                    {
                        break;
                    }

                    object[] args = new object[1];
                    foreach (object item in Items)
                    {
                        args[0] = item;
                        add.Invoke(Target, args);
                    }

                    break;
                }
            }
        }

        /// <summary>集合内容是否与捕获时一致（顺序敏感；Set 只比元素集合）。</summary>
        public bool ContentsEqual(out string detail)
        {
            detail = null;

            switch (Kind)
            {
                case CollectionKind.Array:
                {
                    var array = (Array)Target;
                    if (array.Length != Items.Length)
                    {
                        detail = $"array length {array.Length} != {Items.Length}";
                        return false;
                    }

                    for (int i = 0; i < Items.Length; i++)
                    {
                        if (!SameAsCaptured(array.GetValue(i), Items[i]))
                        {
                            detail = $"array[{i}] changed";
                            return false;
                        }
                    }

                    return true;
                }

                case CollectionKind.List:
                {
                    var list = (IList)Target;
                    if (list.Count != Items.Length)
                    {
                        detail = $"list count {list.Count} != {Items.Length}";
                        return false;
                    }

                    for (int i = 0; i < Items.Length; i++)
                    {
                        if (!SameAsCaptured(list[i], Items[i]))
                        {
                            detail = $"list[{i}] changed";
                            return false;
                        }
                    }

                    return true;
                }

                case CollectionKind.Dictionary:
                {
                    var dictionary = (IDictionary)Target;
                    if (dictionary.Count != Keys.Length)
                    {
                        detail = $"dict count {dictionary.Count} != {Keys.Length}";
                        return false;
                    }

                    for (int i = 0; i < Keys.Length; i++)
                    {
                        if (!dictionary.Contains(Keys[i]))
                        {
                            detail = $"dict key {Describe(Keys[i])} missing";
                            return false;
                        }

                        if (!SameAsCaptured(dictionary[Keys[i]], Values[i]))
                        {
                            detail = $"dict[{Describe(Keys[i])}] changed";
                            return false;
                        }
                    }

                    return true;
                }

                case CollectionKind.Set:
                {
                    var live = new List<object>();
                    foreach (object item in (IEnumerable)Target)
                    {
                        live.Add(item);
                    }

                    if (live.Count != Items.Length)
                    {
                        detail = $"set count {live.Count} != {Items.Length}";
                        return false;
                    }

                    foreach (object item in Items)
                    {
                        bool found = false;
                        foreach (object candidate in live)
                        {
                            if (SameAsCaptured(candidate, item))
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            detail = $"set item {Describe(item)} missing";
                            return false;
                        }
                    }

                    return true;
                }

                default:
                    return true;
            }
        }
    }
}

/// <summary>深拷贝序列化比对的结果。Equal 为 true 才说明"活对象和快照逐字节相同"。</summary>
internal sealed class DeepCompareResult
{
    public bool Equal;
    public string CapturedHash;
    public string LiveHash;
    public string Diff;

    public override string ToString()
    {
        if (Equal)
        {
            return $"deep-equal {CapturedHash}";
        }

        return $"deep-mismatch captured={CapturedHash} live={LiveHash}\n{Diff}";
    }
}

/// <summary>恢复后逐字段核对的结果。Mismatches 为 0 才说明"捕获到的状态全部回来了"。</summary>
internal sealed class VerifyReport
{
    private const int MaxSamples = 12;

    public int Records;
    public int Fields;
    public int Mismatches;
    public bool Truncated;
    public readonly List<string> Samples = new List<string>(MaxSamples);

    public bool Clean => Mismatches == 0;

    public void Add(string message)
    {
        Mismatches++;
        if (Samples.Count < MaxSamples)
        {
            Samples.Add(message);
        }
    }

    public override string ToString()
    {
        string suffix = Truncated ? "（快照被对象数上限截断，未覆盖的部分不在保证内）" : string.Empty;

        if (Mismatches == 0)
        {
            return $"verified {Fields} field(s) across {Records} object(s): all match{suffix}";
        }

        return $"verified {Fields} field(s) across {Records} object(s): {Mismatches} mismatch(es){suffix}; "
               + string.Join(" | ", Samples);
    }
}
