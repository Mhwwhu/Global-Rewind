using System;
using System.Collections.Generic;
using GlobalRewind.Snapshot;

namespace GlobalRewind.Tree;

/// <summary>
/// 探索树上的一个节点 = 战斗在某个"静止决策点"的完整状态。
/// 子节点由"从该状态做出的动作"产生；因此同一父节点可以有多个子节点 —— 这就是分支。
/// </summary>
internal sealed class RewindNode
{
    public int Id;
    public int ParentId;
    public string Label;
    public string Kind;
    public int Round;
    public int Turn;
    public long CreatedUtcTicks;
    public uint Checksum;
    public int ObjectCount;
    public int FieldCount;
    public int SharedObjectCount;
    public int SharedFieldCount;

    /// <summary>
    /// 多人节点的时间坐标（单人局也填，退化成 NextActionId + 回合）。
    /// 合并判据必须同时看它 —— 只看 checksum 会把“同一动作的不同阶段”误合并。
    /// </summary>
    public Frontier Frontier;

    /// <summary>是否所有参与者都已确认持有同一逻辑点的快照（M1 只有本端，固定 false）。</summary>
    public bool Shared;

    /// <summary>当前节点是否处在“全体可操作”的稳定点，允许作为跳转目标。</summary>
    public bool Playable = true;

    /// <summary>sp / local / shared / partial / unstable / stale。</summary>
    public string State = "sp";

    /// <summary>建点时在场的玩家 NetId（排序）。玩家离开/加入后用于判定节点是否失效。</summary>
    public ulong[] Participants = Array.Empty<ulong>();

    /// <summary>只包含共享字段的深拷贝指纹（本进程内比较，不用于跨端）。</summary>
    public string SharedDeepHash;

    /// <summary>
    /// 捕获时整张对象图的规范序列化哈希（逐字节可比的"深拷贝指纹"）。
    /// 跳转后重算一次必须完全相同，否则说明有状态没落回目标字段。
    /// </summary>
    public string DeepHash;

    /// <summary>捕获时的可读状态摘要，用于校验失败时对比。</summary>
    public string Digest;

    /// <summary>
    /// 传送门指向的**规范节点** id；0 表示本节点就是规范节点。
    ///
    /// 同态状态只保留一个可展开的代表（规范节点，取同态节点里最深的一个），
    /// 其它同态节点退化成"传送门"：叶子、没有子节点、永远不会成为 current，
    /// 点击等同点击规范节点。传送门与规范节点共享同一个 <see cref="Snapshot"/> 实例。
    /// </summary>
    public int AliasOf;

    /// <summary>
    /// 合并时是否是**逐字节相同**（DeepHash/MergeHash 命中）。
    /// false = 只是可玩状态相同（checksum + 切面一致，差异在战斗日志/动作编号这类不影响局面的字段）。
    /// </summary>
    public bool MergeExact = true;

    public readonly List<int> Children = new List<int>();

    /// <summary>
    /// 仅存在于内存。落盘的是元数据与摘要；跨进程恢复需要另一条路径（见 docs/design.md）。
    /// </summary>
    public ObjectGraphSnapshot Snapshot;
}

/// <summary>
/// 单场战斗的探索树。编号全局单调递增，剪枝不会回收编号。
/// </summary>
internal sealed class RewindTree
{
    private readonly Dictionary<int, RewindNode> _nodes = new Dictionary<int, RewindNode>();
    private int _nextId = 1;

    public int NextId => _nextId;

    public int CurrentId { get; set; }

    public int Count => _nodes.Count;

    public IReadOnlyDictionary<int, RewindNode> Nodes => _nodes;

    public RewindNode Root
    {
        get
        {
            foreach (RewindNode node in _nodes.Values)
            {
                if (node.ParentId == 0)
                {
                    return node;
                }
            }

            return null;
        }
    }

    public RewindNode Current => Get(CurrentId);

    public RewindNode Get(int id)
    {
        return _nodes.TryGetValue(id, out RewindNode node) ? node : null;
    }

    public void Clear()
    {
        _nodes.Clear();
        _nextId = 1;
        CurrentId = 0;
    }

    /// <summary>
    /// 在 parentId 下新建节点。parentId 为 0 表示根。
    /// </summary>
    public RewindNode Add(int parentId, string label, string kind, ObjectGraphSnapshot snapshot,
        uint checksum, int round, int turn, out string error)
    {
        error = null;

        if (parentId != 0 && !_nodes.ContainsKey(parentId))
        {
            error = $"父节点 #{parentId} 不存在";
            return null;
        }

        var node = new RewindNode
        {
            Id = _nextId++,
            ParentId = parentId,
            Label = label ?? string.Empty,
            Kind = kind ?? "state",
            Round = round,
            Turn = turn,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            Checksum = checksum,
            Snapshot = snapshot,
            ObjectCount = snapshot?.ObjectCount ?? 0,
            FieldCount = snapshot?.FieldCount ?? 0
        };

        _nodes[node.Id] = node;

        if (parentId != 0)
        {
            _nodes[parentId].Children.Add(node.Id);
        }

        CurrentId = node.Id;
        return node;
    }

    /// <summary>
    /// 用**外部指定的编号**建点：多人局里节点 id 由房主分配，客户端只镜像，
    /// 绝不能自己自增（否则两端的树编号会错位，跳转目标在另一端不存在）。
    /// </summary>
    public RewindNode AddWithId(int id, int parentId, string label, string kind,
        ObjectGraphSnapshot snapshot, uint checksum, int round, int turn, out string error)
    {
        error = null;

        if (id <= 0)
        {
            error = $"非法节点编号 #{id}";
            return null;
        }

        if (_nodes.ContainsKey(id))
        {
            error = $"节点 #{id} 已存在";
            return null;
        }

        if (parentId != 0 && !_nodes.ContainsKey(parentId))
        {
            error = $"父节点 #{parentId} 不存在";
            return null;
        }

        var node = new RewindNode
        {
            Id = id,
            ParentId = parentId,
            Label = label ?? string.Empty,
            Kind = kind ?? "state",
            Round = round,
            Turn = turn,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            Checksum = checksum,
            Snapshot = snapshot,
            ObjectCount = snapshot?.ObjectCount ?? 0,
            FieldCount = snapshot?.FieldCount ?? 0
        };

        _nodes[id] = node;
        if (parentId != 0)
        {
            _nodes[parentId].Children.Add(id);
        }

        // 编号空间必须跟着房主走：客户端下次本地建点（如果将来允许）也不能撞号。
        if (id >= _nextId)
        {
            _nextId = id + 1;
        }

        CurrentId = id;
        return node;
    }

    /// <summary>
    /// 剪掉 id 及其整棵子树。根节点拒绝剪枝。当前节点被剪掉时回退到父节点。
    /// </summary>
    public bool Prune(int id, out int removed, out string error)
    {
        return Prune(id, out removed, out _, out error);
    }

    /// <summary>
    /// 剪掉 id 及其全部后代。removedIds 回传被删的节点（含 id 本身），
    /// 供调用方清理落盘文件——只改内存会让磁盘上留下再也访问不到的孤儿摘要。
    /// </summary>
    public bool Prune(int id, out int removed, out List<int> removedIds, out string error)
    {
        removed = 0;
        removedIds = null;
        error = null;

        RewindNode node = Get(id);
        if (node == null)
        {
            error = $"节点 #{id} 不存在";
            return false;
        }

        if (node.ParentId == 0)
        {
            error = "根节点不能剪枝";
            return false;
        }

        var doomed = new List<int>();
        Collect(id, doomed);

        int parentId = node.ParentId;
        RewindNode parent = Get(parentId);
        parent?.Children.Remove(id);

        foreach (int doomedId in doomed)
        {
            _nodes.Remove(doomedId);
        }

        // 规范节点被剪掉时，它原来的传送门不能悬空：重选主节点（见方法注释）。
        ReelectCanonicals(doomed);

        // 防御：任何仍然指向不存在节点的 alias 都扶正，避免 Resolve 走到空气。
        foreach (RewindNode remaining in _nodes.Values)
        {
            if (remaining.AliasOf != 0 && !_nodes.ContainsKey(remaining.AliasOf))
            {
                remaining.AliasOf = 0;
            }
        }

        removed = doomed.Count;
        removedIds = doomed;

        if (doomed.Contains(CurrentId))
        {
            CurrentId = parentId;
        }

        return true;
    }

    /// <summary>
    /// 剪枝后重选规范节点（主节点）：
    ///   · 被删的规范节点如果还有活着的传送门，按“最深优先、同深度取小 id”重新选一个；
    ///   · 只剩一个传送门时直接把它变回普通节点（AliasOf = 0），不需要别的来路指向它；
    ///   · 选中的节点转正，其余传送门改指它并共享同一份快照。
    ///
    /// 判据与建点时完全一致（<see cref="Depth"/> 沿主父链现算），所以多人两端
    /// 重放同一次剪枝会得到同一棵树，不需要额外同步。
    /// </summary>
    private void ReelectCanonicals(ICollection<int> doomed)
    {
        var orphans = new Dictionary<int, List<int>>();
        foreach (RewindNode node in _nodes.Values)
        {
            if (node.AliasOf == 0
                || !doomed.Contains(node.AliasOf)
                || doomed.Contains(node.Id))
            {
                continue;
            }

            if (!orphans.TryGetValue(node.AliasOf, out List<int> portals))
            {
                portals = new List<int>();
                orphans[node.AliasOf] = portals;
            }

            portals.Add(node.Id);
        }

        foreach (List<int> portals in orphans.Values)
        {
            RewindNode best = null;
            int bestDepth = int.MinValue;

            foreach (int portalId in portals)
            {
                RewindNode portal = Get(portalId);
                if (portal == null)
                {
                    continue;
                }

                int depth = Depth(portalId);
                if (depth > bestDepth || (depth == bestDepth && (best == null || portal.Id < best.Id)))
                {
                    best = portal;
                    bestDepth = depth;
                }
            }

            if (best == null)
            {
                continue;
            }

            best.AliasOf = 0;
            if (portals.Count == 1)
            {
                continue;
            }

            foreach (int portalId in portals)
            {
                if (portalId == best.Id)
                {
                    continue;
                }

                RewindNode portal = Get(portalId);
                if (portal == null)
                {
                    continue;
                }

                portal.AliasOf = best.Id;
                portal.Snapshot = best.Snapshot;
                portal.ObjectCount = best.ObjectCount;
                portal.FieldCount = best.FieldCount;
                portal.SharedObjectCount = best.SharedObjectCount;
                portal.SharedFieldCount = best.SharedFieldCount;
            }
        }
    }

    private void Collect(int id, List<int> into)
    {
        into.Add(id);
        RewindNode node = Get(id);
        if (node == null)
        {
            return;
        }

        foreach (int child in node.Children)
        {
            Collect(child, into);
        }
    }

    /// <summary>
    /// 从根到 id 的路径（含两端）。
    /// </summary>
    public List<int> PathToRoot(int id)
    {
        var path = new List<int>();
        int guard = 0;
        while (id != 0 && guard++ < 10000)
        {
            path.Add(id);
            RewindNode node = Get(id);
            if (node == null)
            {
                break;
            }

            id = node.ParentId;
        }

        path.Reverse();
        return path;
    }

    public int FirstChild(int id)
    {
        RewindNode node = Get(id);
        return node != null && node.Children.Count > 0 ? node.Children[0] : 0;
    }

    public int LastChild(int id)
    {
        RewindNode node = Get(id);
        return node != null && node.Children.Count > 0 ? node.Children[node.Children.Count - 1] : 0;
    }

    // ---------------------------------------------------------------- 传送门 / 规范节点

    /// <summary>
    /// 解析到规范节点：跟随 AliasOf（防御性上限 8 跳）。
    /// 悬空 alias（目标不存在）直接把本节点扶正，保证返回值一定满足 AliasOf == 0。
    /// </summary>
    public int Resolve(int id)
    {
        int guard = 0;
        while (guard++ < 8
               && _nodes.TryGetValue(id, out RewindNode node)
               && node.AliasOf != 0)
        {
            id = node.AliasOf;
        }

        if (!_nodes.TryGetValue(id, out RewindNode resolved))
        {
            return 0;
        }

        if (resolved.AliasOf != 0)
        {
            resolved.AliasOf = 0;
        }

        return resolved.Id;
    }

    public bool IsPortal(int id)
    {
        RewindNode node = Get(id);
        return node != null && node.AliasOf != 0;
    }

    /// <summary>节点深度：根 = 0，沿主父链现算（重新挂父后自动正确，不缓存）。</summary>
    public int Depth(int id)
    {
        int depth = -1;
        int guard = 0;
        while (id != 0 && guard++ < 10000 && _nodes.TryGetValue(id, out RewindNode node))
        {
            depth++;
            id = node.ParentId;
        }

        return depth;
    }

    public List<int> PortalsOf(int id)
    {
        var result = new List<int>();
        foreach (RewindNode node in _nodes.Values)
        {
            if (node.AliasOf == id)
            {
                result.Add(node.Id);
            }
        }

        result.Sort();
        return result;
    }

    public int PortalCount(int id)
    {
        int count = 0;
        foreach (RewindNode node in _nodes.Values)
        {
            if (node.AliasOf == id)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>ancestorId 是否是 nodeId 的主干祖先（不含自身）。</summary>
    public bool IsAncestor(int ancestorId, int nodeId)
    {
        int guard = 0;
        int id = nodeId;
        while (id != 0 && guard++ < 10000 && _nodes.TryGetValue(id, out RewindNode node))
        {
            if (node.ParentId == ancestorId)
            {
                return true;
            }

            id = node.ParentId;
        }

        return false;
    }

    /// <summary>
    /// 建一个传送门：普通节点槽位上的叶子，AliasOf 指向规范节点，并**共享**它的 memento。
    /// 不复制快照，也不推进“当前节点”—— current 由调用方设成规范节点。
    /// </summary>
    public RewindNode AddPortal(int parentId, int canonicalId, string label, string kind, out string error)
    {
        error = null;

        if (parentId != 0 && !_nodes.ContainsKey(parentId))
        {
            error = $"父节点 #{parentId} 不存在";
            return null;
        }

        int resolvedId = Resolve(canonicalId);
        RewindNode canonical = Get(resolvedId);
        if (canonical == null)
        {
            error = $"规范节点 #{canonicalId} 不存在";
            return null;
        }

        if (parentId != 0 && IsAncestor(resolvedId, parentId))
        {
            // 规范节点是父节点的祖先：说明这一步是空转，调用方本不该建传送门。
            error = $"规范节点 #{resolvedId} 是 #{parentId} 的祖先";
            return null;
        }

        var portal = new RewindNode
        {
            Id = _nextId++,
            ParentId = parentId,
            AliasOf = resolvedId,
            Label = label ?? string.Empty,
            Kind = kind ?? "state",
            Round = canonical.Round,
            Turn = canonical.Turn,
            CreatedUtcTicks = DateTime.UtcNow.Ticks,
            Checksum = canonical.Checksum,
            Snapshot = canonical.Snapshot,
            ObjectCount = canonical.ObjectCount,
            FieldCount = canonical.FieldCount,
            SharedObjectCount = canonical.SharedObjectCount,
            SharedFieldCount = canonical.SharedFieldCount,
            SharedDeepHash = canonical.SharedDeepHash,
            DeepHash = canonical.DeepHash,
            Digest = canonical.Digest,
            Frontier = canonical.Frontier,
            Participants = canonical.Participants
        };

        _nodes[portal.Id] = portal;
        if (parentId != 0)
        {
            _nodes[parentId].Children.Add(portal.Id);
        }

        CurrentId = resolvedId;
        return portal;
    }

    /// <summary>
    /// 换主：newId（刚建的、更深的同态节点）接管 oldId 的规范身份。
    ///
    /// 顺序固定：先搬孩子（保持顺序）、再把旧主清成叶子传送门、最后把所有指向旧主的传送门改指新主。
    /// 新节点不可能落在旧主子树里（调用方已用“祖先/后代 → 不建点”挡住），这里再守一次。
    /// </summary>
    public bool DemoteCanonical(int oldId, int newId, out int movedChildren, out string error)
    {
        movedChildren = 0;
        error = null;

        RewindNode oldNode = Get(oldId);
        RewindNode newNode = Get(newId);
        if (oldNode == null || newNode == null)
        {
            error = $"节点不存在（old #{oldId} / new #{newId}）";
            return false;
        }

        if (oldNode.AliasOf != 0)
        {
            error = $"#{oldId} 不是规范节点";
            return false;
        }

        if (oldId == newId || IsAncestor(oldId, newId))
        {
            error = $"#{newId} 在 #{oldId} 的子树里，拒绝换主";
            return false;
        }

        foreach (int childId in oldNode.Children.ToArray())
        {
            RewindNode child = Get(childId);
            if (child == null)
            {
                continue;
            }

            child.ParentId = newId;
            newNode.Children.Add(childId);
            movedChildren++;
        }

        oldNode.Children.Clear();

        // 旧主退化成叶子传送门：共享新主的快照与指纹，不再持有自己的 memento。
        oldNode.AliasOf = newId;
        CopySnapshotFingerprint(oldNode, newNode);

        foreach (RewindNode node in _nodes.Values)
        {
            if (node.AliasOf != oldId)
            {
                continue;
            }

            node.AliasOf = newId;
            CopySnapshotFingerprint(node, newNode);
        }

        CurrentId = newId;
        return true;
    }

    private static void CopySnapshotFingerprint(RewindNode target, RewindNode source)
    {
        target.Snapshot = source.Snapshot;
        target.DeepHash = source.DeepHash;
        target.SharedDeepHash = source.SharedDeepHash;
        target.ObjectCount = source.ObjectCount;
        target.FieldCount = source.FieldCount;
        target.SharedObjectCount = source.SharedObjectCount;
        target.SharedFieldCount = source.SharedFieldCount;
        target.Digest = source.Digest;
    }
}
