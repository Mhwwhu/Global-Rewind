namespace GlobalRewind.Snapshot;

/// <summary>
/// 快照字段的作用域：
///   · <see cref="Shared"/>：所有玩家共同状态（跨端必须一致）；
///   · <see cref="Local"/>：只属于本机视角（本地动作禁用标志、队列阶段）；
///   · <see cref="Excluded"/>：不进快照（锁 / TCS / 传输态）。
///
/// 恢复时 Shared + Local 都会写回；跨端比较只取 Shared。
/// </summary>
internal enum SnapshotScope
{
    Shared,
    Local,
    Excluded
}

/// <summary>规范序列化时取哪些记录/字段。</summary>
internal enum HashScope
{
    Full,
    Shared,
    /// <summary>
    /// Full 去掉“已分配但还没执行的动作编号”（`ActionQueueSet._nextId`）。
    /// 用于判断两个节点是不是**同一个模型状态**：快速连点时，后一个动作在
    /// 前一个动作结算的瞬间就已经占了一个编号，模型完全相同、只有计数器不同。
    /// 合并节点时用这个口径，跳转/恢复仍然用 Full。
    /// </summary>
    Merge,
    SharedMerge
}

/// <summary>
/// 一个快照根 + 它的作用域。共享根必须排在本地根前面：
/// 这样同一个对象先从共享路径被访问到，就标记为 Shared；
/// 本地根只会把“只从本地路径可达”的对象标成 Local。
/// </summary>
internal readonly struct SnapshotRoot
{
    public readonly object Value;
    public readonly SnapshotScope Scope;
    public readonly string Name;

    public SnapshotRoot(object value, SnapshotScope scope, string name)
    {
        Value = value;
        Scope = scope;
        Name = name ?? string.Empty;
    }
}
