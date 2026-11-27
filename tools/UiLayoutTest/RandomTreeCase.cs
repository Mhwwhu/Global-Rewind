using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalRewind.Presentation;

namespace UiLayoutTest;

internal sealed class TestNode
{
    public readonly int Id;
    public readonly int ParentId;
    public readonly List<int> Children = new List<int>();

    public TestNode(int id, int parentId)
    {
        Id = id;
        ParentId = parentId;
    }
}

/// <summary>
/// 测试用的可变树（只保留 UI 需要的父子关系），以及对应的随机操作序列。
/// 与 RewindTree 无关 —— 这正是 UI 层解耦后可以独立做随机测试的原因。
/// </summary>
internal enum TreeShape
{
    Random,
    Wide,
    Deep,
    Balanced
}

internal sealed class RandomTreeCase
{
    private readonly Random _rng;
    private readonly Dictionary<int, TestNode> _nodes = new Dictionary<int, TestNode>();

    public readonly HashSet<int> Collapsed = new HashSet<int>();
    public readonly List<string> OpLog = new List<string>();
    public readonly Dictionary<string, int> OpCounts = new Dictionary<string, int>();

    public int Seed { get; }
    public TreeShape Shape { get; }
    public int MinNodes { get; }
    public int MaxNodes { get; }
    public int NextId { get; private set; } = 2;

    public RandomTreeCase(int seed, int initialNodes, int minNodes, int maxNodes, TreeShape shape)
    {
        Seed = seed;
        Shape = shape;
        MinNodes = minNodes;
        MaxNodes = maxNodes;
        _rng = new Random(seed);

        _nodes[1] = new TestNode(1, 0);

        while (Count < initialNodes)
        {
            int parentId = PickParentForGrow();
            AddChild(parentId);
        }

        OpCounts["grow"] = initialNodes - 1;
        OpLog.Add($"grow to {initialNodes} nodes");
    }

    public int Count => _nodes.Count;

    public TestNode Get(int id)
    {
        return _nodes.TryGetValue(id, out TestNode node) ? node : null;
    }

    public TestNode AddChild(int parentId)
    {
        TestNode parent = Get(parentId);
        if (parent == null)
        {
            throw new InvalidOperationException($"parent #{parentId} not found");
        }

        var node = new TestNode(NextId++, parentId);
        _nodes[node.Id] = node;
        parent.Children.Add(node.Id);
        return node;
    }

    /// <summary>随机执行一次 create / prune / collapse / expand，返回这次操作的描述。</summary>
    public string ApplyRandomOperation()
    {
        double roll = _rng.NextDouble();
        bool canAdd = Count < MaxNodes;
        bool canPrune = PickPrunable(out int prunableId, out int prunableSize);
        bool canCollapse = PickCollapsible(out int collapsibleId);
        bool canExpand = Collapsed.Count > 0;

        string op;
        if (roll < 0.35 && canAdd)
        {
            op = "add";
        }
        else if (roll < 0.55 && canPrune)
        {
            op = "prune";
        }
        else if (roll < 0.80 && canCollapse)
        {
            op = "collapse";
        }
        else if (canExpand)
        {
            op = "expand";
        }
        else if (canAdd)
        {
            op = "add";
        }
        else if (canPrune)
        {
            op = "prune";
        }
        else if (canCollapse)
        {
            op = "collapse";
        }
        else
        {
            throw new InvalidOperationException("no valid operation");
        }

        string description;
        switch (op)
        {
            case "add":
            {
                int parentId = PickParentForGrow();
                TestNode node = AddChild(parentId);
                description = $"add #{node.Id} -> parent #{parentId}";
                break;
            }
            case "prune":
            {
                int removed = Prune(prunableId);
                description = $"prune #{prunableId} (subtree {prunableSize}, removed {removed})";
                break;
            }
            case "collapse":
            {
                Collapsed.Add(collapsibleId);
                description = $"collapse #{collapsibleId}";
                break;
            }
            default:
            {
                int[] ids = Collapsed.ToArray();
                int id = ids[_rng.Next(ids.Length)];
                Collapsed.Remove(id);
                description = $"expand #{id}";
                break;
            }
        }

        RemoveStaleCollapsed();
        OpLog.Add($"step {OpLog.Count}: {description} | nodes={Count} collapsed={Collapsed.Count}");
        OpCounts[op] = OpCounts.TryGetValue(op, out int count) ? count + 1 : 1;
        return description;
    }

    public int Depth(int id)
    {
        int depth = 0;
        int guard = 0;
        while (id != 0 && guard++ < 100000 && Get(id) != null)
        {
            id = Get(id).ParentId;
            depth++;
        }

        return depth;
    }

    public int MaxDepth()
    {
        int max = 0;
        foreach (int id in _nodes.Keys)
        {
            max = Math.Max(max, Depth(id));
        }

        return max;
    }

    public int SubtreeSize(int id)
    {
        int size = 0;
        foreach (int _ in SubtreeIds(id))
        {
            size++;
        }

        return size;
    }

    public List<int> SubtreeIds(int id)
    {
        var result = new List<int>();
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int current = stack.Pop();
            TestNode node = Get(current);
            if (node == null)
            {
                continue;
            }

            result.Add(current);
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }

        return result;
    }

    public TreeOverlaySnapshot BuildSnapshot()
    {
        var nodes = new List<TreeNodeView>(Count);
        AddSnapshotNode(1, 0, nodes);

        return new TreeOverlaySnapshot
        {
            Title = "ui-layout-test",
            Subtitle = $"{Count} nodes",
            Hint = "test",
            EmptyText = "empty",
            FocusedId = 0,
            SessionKey = 1,
            Nodes = nodes
        };
    }

    public string DumpTree()
    {
        var builder = new StringBuilder();
        DumpNode(1, 0, builder);
        return builder.ToString();
    }

    private void DumpNode(int id, int indent, StringBuilder builder)
    {
        TestNode node = Get(id);
        if (node == null)
        {
            return;
        }

        builder.Append(' ', indent * 2).Append('#').Append(id);
        if (Collapsed.Contains(id))
        {
            builder.Append(" [collapsed]");
        }

        builder.Append(" -> ").Append(string.Join(",", node.Children)).AppendLine();
        foreach (int child in node.Children)
        {
            DumpNode(child, indent + 1, builder);
        }
    }

    private int AddSnapshotNode(int id, int parentId, List<TreeNodeView> into)
    {
        TestNode node = Get(id);
        if (node == null)
        {
            return 0;
        }

        var view = new TreeNodeView
        {
            Id = id,
            ParentId = parentId,
            Children = node.Children.ToArray(),
            Label = id.ToString(),
            Details = $"#{id}",
            SubtreeSize = 1
        };

        into.Add(view);

        int size = 1;
        foreach (int child in node.Children)
        {
            size += AddSnapshotNode(child, id, into);
        }

        view.SubtreeSize = size;
        return size;
    }

    private int Prune(int id)
    {
        TestNode node = Get(id);
        if (node == null || id == 1)
        {
            return 0;
        }

        TestNode parent = Get(node.ParentId);
        if (parent == null)
        {
            return 0;
        }

        List<int> removedIds = SubtreeIds(id);
        parent.Children.Remove(id);
        foreach (int removed in removedIds)
        {
            _nodes.Remove(removed);
            Collapsed.Remove(removed);
        }

        return removedIds.Count;
    }

    private bool PickPrunable(out int id, out int size)
    {
        id = 0;
        size = 0;

        var candidates = new List<int>();
        foreach (int candidate in _nodes.Keys)
        {
            if (candidate == 1)
            {
                continue;
            }

            int candidateSize = SubtreeSize(candidate);
            if (Count - candidateSize >= MinNodes)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        id = candidates[_rng.Next(candidates.Count)];
        size = SubtreeSize(id);
        return true;
    }

    private bool PickCollapsible(out int id)
    {
        var candidates = new List<int>();
        foreach (int candidate in _nodes.Keys)
        {
            if (_nodes[candidate].Children.Count > 0 && !Collapsed.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count == 0)
        {
            id = 0;
            return false;
        }

        id = candidates[_rng.Next(candidates.Count)];
        return true;
    }

    private void RemoveStaleCollapsed()
    {
        var stale = new List<int>();
        foreach (int id in Collapsed)
        {
            if (!_nodes.ContainsKey(id))
            {
                stale.Add(id);
            }
        }

        foreach (int id in stale)
        {
            Collapsed.Remove(id);
        }
    }

    private int PickParentForGrow()
    {
        int[] alive = _nodes.Keys.ToArray();
        if (alive.Length == 0)
        {
            return 1;
        }

        double roll = _rng.NextDouble();

        switch (Shape)
        {
            case TreeShape.Wide:
                // 尽量浅：故意造出"根/前几层挂很多孩子"的宽树。
                if (roll < 0.55)
                {
                    return 1;
                }

                if (roll < 0.85)
                {
                    var shallow = new List<int>();
                    foreach (int id in alive)
                    {
                        if (Depth(id) <= 3)
                        {
                            shallow.Add(id);
                        }
                    }

                    if (shallow.Count > 0)
                    {
                        return shallow[_rng.Next(shallow.Count)];
                    }
                }

                return alive[_rng.Next(alive.Length)];

            case TreeShape.Deep:
                // 尽量深：长链为主，夹一点叶子分叉。
                if (roll < 0.65)
                {
                    int last = NextId - 1;
                    if (last >= 1 && _nodes.ContainsKey(last))
                    {
                        return last;
                    }
                }

                if (roll < 0.90)
                {
                    var leaves = new List<int>();
                    foreach (int id in alive)
                    {
                        if (_nodes[id].Children.Count == 0)
                        {
                            leaves.Add(id);
                        }
                    }

                    if (leaves.Count > 0)
                    {
                        return leaves[_rng.Next(leaves.Count)];
                    }
                }

                return alive[_rng.Next(alive.Length)];

            case TreeShape.Balanced:
                // 最浅且孩子最少的节点：近似完全二叉树。
                return PickBalancedParent();

            default:
                if (roll < 0.15)
                {
                    int last = NextId - 1;
                    if (last >= 1 && _nodes.ContainsKey(last))
                    {
                        return last;
                    }
                }

                if (roll < 0.50)
                {
                    var leaves = new List<int>();
                    foreach (int id in alive)
                    {
                        if (_nodes[id].Children.Count == 0)
                        {
                            leaves.Add(id);
                        }
                    }

                    if (leaves.Count > 0)
                    {
                        return leaves[_rng.Next(leaves.Count)];
                    }
                }

                return alive[_rng.Next(alive.Length)];
        }
    }

    private int PickBalancedParent()
    {
        // 按层序找第一个还没满 2 个孩子的节点 —— 填满一层再下一层，近似完全二叉树。
        var queue = new Queue<int>();
        queue.Enqueue(1);
        while (queue.Count > 0)
        {
            int id = queue.Dequeue();
            TestNode node = Get(id);
            if (node == null)
            {
                continue;
            }

            if (node.Children.Count < 2)
            {
                return id;
            }

            foreach (int child in node.Children)
            {
                queue.Enqueue(child);
            }
        }

        return 1;
    }
}
