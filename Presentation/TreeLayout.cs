using System;
using System.Collections.Generic;

namespace GlobalRewind.Presentation;

/// <summary>
/// 探索树的几何布局（不涉及任何 Godot 节点，也不涉及任何战斗语义）。
///
/// 输入是 <see cref="TreeOverlaySnapshot"/>（纯数据）与一组"被折叠的节点 id"，
/// 输出是每个节点/连线的坐标与可见性。UI 层可以脱离模组逻辑单独跑：
/// 给一份任意形状的快照就能画出一棵树。
///
/// 布局约定（UI 需求，改之前先想清楚）：
///   · 第一个子节点永远在父节点**正下方**，父子的 X 完全相同；
///   · 其余子节点与第一个子节点同高，从左往右排；
///   · 第 i 个子节点放在「第 i-1 个子节点子树最右侧节点」的右边，
///     间距固定为 <see cref="SiblingGap"/> —— 于是任意两棵子树都不会重叠，且尽量贴近；
///   · 被折叠的节点在 UI 上等同于**没有子节点的节点**，因此整棵树重新排版。
///
/// 折叠状态属于 UI（<see cref="HashSet{T}"/> of id），不写回数据源：
/// 折叠只是显示方式，不影响节点、编号和分支语义。子树里的折叠状态会一直保留，
/// 展开外层时里层仍然折叠。
/// </summary>
internal sealed class TreeLayout
{
    public const float NodeDiameter = 32f;
    public const float SiblingGap = 30f;
    public const float LevelHeight = 84f;
    public const float Padding = 40f;

    internal sealed class Entry
    {
        public int Id;
        public TreeNodeView View;

        /// <summary>圆心坐标（自然坐标，未乘缩放）。</summary>
        public float X;
        public float Y;

        /// <summary>子树最右侧圆心相对本节点圆心的偏移（叶子为 0）。</summary>
        public float RightExtent;

        /// <summary>本节点在 UI 上是折叠状态（等同于叶子）。</summary>
        public bool Collapsed;

        /// <summary>真实子树节点数（含自身，不受折叠影响）。</summary>
        public int SubtreeCount = 1;

        /// <summary>折叠时被隐藏的节点数（含本节点）。</summary>
        public int HiddenCount;

        /// <summary>顶层分支配色索引；根节点为 -1（使用强调色）。</summary>
        public int ColorIndex;

        public readonly List<Entry> Children = new List<Entry>();

        public float OffsetFromParent;
    }

    public readonly Dictionary<int, Entry> ById = new Dictionary<int, Entry>();
    public readonly List<Entry> All = new List<Entry>();

    /// <summary>被折叠隐藏的节点 → 它所属折叠区（空心圆）的 id。连线收口用。</summary>
    public readonly Dictionary<int, int> HiddenOwner = new Dictionary<int, int>();

    /// <summary>传送门 → 规范节点的连线（已在折叠后筛过、收口过）。</summary>
    public readonly List<LinkRoute> Links = new List<LinkRoute>();

    internal struct PointF
    {
        public float X;
        public float Y;

        public PointF(float x, float y)
        {
            X = x;
            Y = y;
        }
    }

    internal sealed class LinkRoute
    {
        public int From;
        public int To;

        /// <summary>可见落点：目标被折叠时是折叠区 id；否则等于 To。</summary>
        public int AnchorTo;

        /// <summary>tooltip 文案（由快照带下来，布局层不解释内容）。</summary>
        public string Details = string.Empty;

        /// <summary>同层连线从目标底部进入，箭头朝上；其余情况落在上方边，箭头朝下。</summary>
        public bool ArrowUp;

        /// <summary>折线顶点（自然坐标，未乘缩放）。</summary>
        public readonly List<PointF> Points = new List<PointF>();
    }

    public int RootId { get; private set; }

    /// <summary>画布自然尺寸（含左右/上下留白）。</summary>
    public float Width { get; private set; }
    public float Height { get; private set; }

    public bool IsEmpty => All.Count == 0;

    public static TreeLayout Build(TreeOverlaySnapshot snapshot, HashSet<int> collapsed)
    {
        var layout = new TreeLayout();
        if (snapshot?.Nodes == null || snapshot.Nodes.Count == 0)
        {
            return layout;
        }

        var byId = new Dictionary<int, TreeNodeView>(snapshot.Nodes.Count);
        foreach (TreeNodeView node in snapshot.Nodes)
        {
            if (node != null)
            {
                byId[node.Id] = node;
            }
        }

        TreeNodeView rootView = null;
        foreach (TreeNodeView node in snapshot.Nodes)
        {
            if (node != null && node.ParentId == 0)
            {
                rootView = node;
                break;
            }
        }

        rootView ??= snapshot.Nodes[0];
        if (rootView == null)
        {
            return layout;
        }

        Entry rootEntry = layout.BuildVisible(byId, rootView, null, -1, collapsed ?? EmptySet);
        if (rootEntry == null)
        {
            return layout;
        }

        layout.RootId = rootEntry.Id;
        layout.Measure(rootEntry);
        layout.Place(rootEntry, 0f, 0f);

        float minX = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = 0f;
        foreach (Entry entry in layout.All)
        {
            minX = Math.Min(minX, entry.X);
            maxX = Math.Max(maxX, entry.X);
            maxY = Math.Max(maxY, entry.Y);
        }

        // 统一平移：最左圆心对齐到 Padding + r，最上圆心对齐到 Padding + r。
        float shiftX = Padding + NodeDiameter / 2f - minX;
        float shiftY = Padding + NodeDiameter / 2f;
        foreach (Entry entry in layout.All)
        {
            entry.X += shiftX;
            entry.Y += shiftY;
        }

        layout.Width = (maxX - minX) + NodeDiameter + Padding * 2f;
        layout.Height = maxY + NodeDiameter + Padding * 2f;

        layout.BuildLinkRoutes(snapshot);
        return layout;
    }

    // ---------------------------------------------------------------- 传送门连线

    /// <summary>传送门圆点画出来的半径（22px 的小圆）。</summary>
    private const float PortalRadius = 11f;

    /// <summary>
    /// 把"传送门 → 规范节点"的连线铺成直角折线：
    ///
    ///   ① 从传送门**底部**出线，竖直落到下一层空带的中线（两行之间没有节点）；
    ///   ② 在这个空带里横向走向目标列——目标就在下一层时一步到位；
    ///   ③ 需要跨层时，先横移到目标旁的空位（兄弟间距 30px 的中线），再竖直走；
    ///   ④ 收口在目标**上方那条边的中点**（层高的一半处），箭头朝下；
    ///   ⑤ **同层**例外：折线走下半层，到目标正下方直接进圆点（落点在圆的下缘），箭头朝上。
    ///
    /// 目标被折叠隐藏时，收口到它所属折叠区的空心圆（单跳，不做商图）。
    /// </summary>
    private void BuildLinkRoutes(TreeOverlaySnapshot snapshot)
    {
        if (snapshot?.Links == null || snapshot.Links.Count == 0 || ById.Count == 0)
        {
            return;
        }

        float half = LevelHeight / 2f;
        var levels = BuildLevelIndex(out List<float> levelYs);
        var usedLanes = new List<(float X, float Y0, float Y1)>();

        var links = new List<TreeLinkView>(snapshot.Links);
        links.Sort((a, b) => a.To != b.To ? a.To.CompareTo(b.To) : a.From.CompareTo(b.From));

        foreach (TreeLinkView link in links)
        {
            if (!ById.TryGetValue(link.From, out Entry source))
            {
                continue; // 源在折叠区里：线跟着隐藏
            }

            if (!ById.TryGetValue(link.To, out Entry target))
            {
                // 目标被折叠：收口到折叠区；连折叠区都找不到就放弃。
                if (!HiddenOwner.TryGetValue(link.To, out int ownerId)
                    || !ById.TryGetValue(ownerId, out target))
                {
                    continue;
                }
            }

            if (ReferenceEquals(source, target))
            {
                continue;
            }

            float exitY = source.Y + (source.View.IsPortal ? PortalRadius : NodeDiameter / 2f);
            float bandY = source.Y + half; // 源下一层空带中线
            float landY = target.Y - half; // 目标上方那条边的中点

            var route = new LinkRoute
            {
                From = link.From,
                To = link.To,
                AnchorTo = target.Id,
                Details = link.Details ?? string.Empty
            };

            AddPoint(route.Points, source.X, exitY);
            AddPoint(route.Points, source.X, bandY);

            if (Math.Abs(source.Y - target.Y) < 0.5f)
            {
                // 同层：折线走**下半层**，到目标正下方直接进主节点（落点在圆的下缘）。
                AddPoint(route.Points, target.X, bandY);
                AddPoint(route.Points, target.X, target.Y + NodeDiameter / 2f);
                route.ArrowUp = true;
            }
            else if (Math.Abs(bandY - landY) < 0.5f)
            {
                // 目标就在下一层：空带里直接横移到位，落点正好是目标上方那条边的中点。
                AddPoint(route.Points, target.X, bandY);
            }
            else
            {
                // 跨层：横移到目标旁的空位 → 空位里竖直走 → 横 31px 收口。
                float laneX = FindGutter(target, bandY, landY, levels, levelYs, usedLanes, source.X);
                usedLanes.Add((laneX, Math.Min(bandY, landY), Math.Max(bandY, landY)));

                AddPoint(route.Points, laneX, bandY);
                AddPoint(route.Points, laneX, landY);
                AddPoint(route.Points, target.X, landY);
            }

            if (route.Points.Count < 2)
            {
                continue;
            }

            Links.Add(route);

            float maxX = 0f;
            float maxY = 0f;
            foreach (PointF point in route.Points)
            {
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }

            Width = Math.Max(Width, maxX + NodeDiameter / 2f + Padding);
            Height = Math.Max(Height, maxY + NodeDiameter / 2f + Padding);
        }
    }

    /// <summary>
    /// 目标旁可以竖直走的空位：优先右侧（父总线从左侧过来，右侧干净），
    /// 其次左侧，再次目标正上方（只在中间层没有节点时合法），最后退回源列
    /// （传送门是叶子，它正下方永远是空的）。
    /// </summary>
    private float FindGutter(Entry target, float y0, float y1,
        Dictionary<float, List<float>> levels, List<float> levelYs,
        List<(float X, float Y0, float Y1)> used, float fallbackX)
    {
        float low = Math.Min(y0, y1);
        float high = Math.Max(y0, y1);
        float r = NodeDiameter / 2f;

        float[] candidates =
        {
            target.X + r + SiblingGap / 2f,
            target.X - r - SiblingGap / 2f,
            target.X,
            fallbackX
        };

        foreach (float candidate in candidates)
        {
            if (candidate < PortalRadius)
            {
                continue;
            }

            if (IsLaneClear(candidate, low, high, levels, levelYs, r)
                && !LaneConflict(candidate, low, high, used))
            {
                return candidate;
            }
        }

        return fallbackX;
    }

    private static void AddPoint(List<PointF> points, float x, float y)
    {
        if (points.Count > 0)
        {
            PointF last = points[points.Count - 1];
            if (Math.Abs(last.X - x) < 0.5f && Math.Abs(last.Y - y) < 0.5f)
            {
                return;
            }
        }

        points.Add(new PointF(x, y));
    }

    /// <summary>每个层高的节点圆心 X（升序）。</summary>
    private Dictionary<float, List<float>> BuildLevelIndex(out List<float> levelYs)
    {
        var levels = new Dictionary<float, List<float>>();
        foreach (Entry entry in All)
        {
            float y = RoundY(entry.Y);
            if (!levels.TryGetValue(y, out List<float> xs))
            {
                xs = new List<float>();
                levels[y] = xs;
            }

            xs.Add(entry.X);
        }

        foreach (List<float> xs in levels.Values)
        {
            xs.Sort();
        }

        levelYs = new List<float>(levels.Keys);
        levelYs.Sort();
        return levels;
    }

    private static float RoundY(float value)
    {
        return (float)System.Math.Round(value, 1);
    }

    /// <summary>把被折叠子树里的每个节点记到折叠区 id 上，连线收口用。</summary>
    private void RecordHiddenOwner(Dictionary<int, TreeNodeView> byId, TreeNodeView view, int ownerId)
    {
        foreach (int childId in view.Children)
        {
            if (!byId.TryGetValue(childId, out TreeNodeView child) || child == null)
            {
                continue;
            }

            HiddenOwner[childId] = ownerId;
            RecordHiddenOwner(byId, child, ownerId);
        }
    }

    private static bool IsLaneClear(float x, float low, float high,
        Dictionary<float, List<float>> levels, List<float> levelYs, float r)
    {
        float clearance = r + 6f;

        foreach (float level in levelYs)
        {
            if (level < low - 0.5f || level > high + 0.5f)
            {
                continue;
            }

            if (!levels.TryGetValue(level, out List<float> xs))
            {
                continue;
            }

            foreach (float nodeX in xs)
            {
                if (Math.Abs(nodeX - x) < clearance)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool LaneConflict(float x, float low, float high,
        List<(float X, float Y0, float Y1)> used)
    {
        foreach ((float otherX, float y0, float y1) in used)
        {
            if (Math.Abs(otherX - x) >= 14f)
            {
                continue;
            }

            if (high < y0 - 0.5f || low > y1 + 0.5f)
            {
                continue;
            }

            return true;
        }

        return false;
    }


    private static readonly HashSet<int> EmptySet = new HashSet<int>();

    private Entry BuildVisible(Dictionary<int, TreeNodeView> byId, TreeNodeView view, Entry parent,
        int colorIndex, HashSet<int> collapsed)
    {
        var entry = new Entry
        {
            Id = view.Id,
            View = view,
            ColorIndex = colorIndex,
            SubtreeCount = Math.Max(1, view.SubtreeSize)
        };

        All.Add(entry);
        ById[view.Id] = entry;
        parent?.Children.Add(entry);

        bool hasChildren = view.Children.Length > 0;
        if (hasChildren && collapsed.Contains(view.Id))
        {
            entry.Collapsed = true;
            entry.HiddenCount = entry.SubtreeCount;
            RecordHiddenOwner(byId, view, view.Id);
            return entry;
        }

        for (int i = 0; i < view.Children.Length; i++)
        {
            if (!byId.TryGetValue(view.Children[i], out TreeNodeView childView) || childView == null)
            {
                continue;
            }

            // 第一个子节点继承父分支的颜色（一条链保持同色），
            // 之后每多一个兄弟就顺延一个颜色，分叉一眼能区分。
            int childColor = colorIndex < 0 ? i : colorIndex + i;
            BuildVisible(byId, childView, entry, childColor, collapsed);
        }

        return entry;
    }

    /// <summary>
    /// 递归测量子树宽度：把每个子节点放在前一个兄弟子树最右节点的右边，
    /// 返回本子树最右侧圆心相对本节点的偏移。
    /// </summary>
    private float Measure(Entry entry)
    {
        if (entry.Children.Count == 0)
        {
            entry.RightExtent = 0f;
            return 0f;
        }

        float previousRight = 0f;
        for (int i = 0; i < entry.Children.Count; i++)
        {
            Entry child = entry.Children[i];
            float childExtent = Measure(child);

            float offset = i == 0
                ? 0f
                : previousRight + NodeDiameter + SiblingGap;

            child.OffsetFromParent = offset;
            previousRight = offset + childExtent;
        }

        entry.RightExtent = previousRight;
        return previousRight;
    }

    private void Place(Entry entry, float x, float y)
    {
        entry.X = x;
        entry.Y = y;

        foreach (Entry child in entry.Children)
        {
            Place(child, x + child.OffsetFromParent, y + LevelHeight);
        }
    }
}
