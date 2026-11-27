using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GlobalRewind.Presentation;

namespace UiLayoutTest;

internal readonly struct BBox
{
    public readonly float MinX;
    public readonly float MaxX;
    public readonly float MinY;
    public readonly float MaxY;

    public BBox(float minX, float maxX, float minY, float maxY)
    {
        MinX = minX;
        MaxX = maxX;
        MinY = minY;
        MaxY = maxY;
    }

    public bool OverlapsY(BBox other, float eps)
    {
        return MaxY > other.MinY + eps && other.MaxY > MinY + eps;
    }
}

internal sealed class ValidationReport
{
    public readonly List<string> Errors = new List<string>();
    public int VisibleNodes;
    public int MaxDepth;
    public int MaxChildren;
    public float CanvasWidth;
    public float CanvasHeight;
    public double MinSameLevelGap = double.MaxValue;
    public double MaxSiblingGapDeviation;
    public double MinEdgeNodeDistance = double.MaxValue;
    public int SubtreePairsChecked;
    public int EdgeNodePairsChecked;

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// UI 布局不变量。所有检查都在"自然坐标"（未乘 zoom）上做；
/// zoom 只是统一乘系数，不会改变这些相对关系。
/// </summary>
internal static class LayoutValidator
{
    private const float Eps = 1e-3f;

    public static ValidationReport Validate(TreeLayout layout, RandomTreeCase test,
        TreeOverlaySnapshot snapshot, string operation, int step)
    {
        var report = new ValidationReport();
        float radius = TreeLayout.NodeDiameter / 2f;

        if (layout.IsEmpty)
        {
            report.Errors.Add("layout is empty");
            return report;
        }

        if (layout.RootId != 1)
        {
            report.Errors.Add($"root id is {layout.RootId}, expected 1");
        }

        if (layout.All.Count != layout.ById.Count)
        {
            report.Errors.Add($"All.Count {layout.All.Count} != ById.Count {layout.ById.Count}");
        }

        report.VisibleNodes = layout.All.Count;
        report.MaxChildren = layout.All.Count == 0 ? 0 : layout.All.Max(e => e.Children.Count);
        report.CanvasWidth = layout.Width;
        report.CanvasHeight = layout.Height;

        // 期望的可见节点集合：遇到折叠的且有子节点的节点就停下。
        var expectedVisible = new HashSet<int>();
        CollectExpectedVisible(test, 1, expectedVisible);
        if (expectedVisible.Count != layout.All.Count)
        {
            report.Errors.Add($"visible count {layout.All.Count} != expected {expectedVisible.Count}");
        }

        foreach (int id in expectedVisible)
        {
            if (!layout.ById.ContainsKey(id))
            {
                report.Errors.Add($"expected visible node #{id} missing");
            }
        }

        foreach (TreeLayout.Entry entry in layout.All)
        {
            if (!expectedVisible.Contains(entry.Id))
            {
                report.Errors.Add($"hidden node #{entry.Id} is rendered");
            }

            if (!IsFinite(entry.X) || !IsFinite(entry.Y))
            {
                report.Errors.Add($"node #{entry.Id} has non-finite position ({entry.X},{entry.Y})");
            }
        }

        // 可见父/深度/子树包围盒
        var parentOf = new Dictionary<int, int>();
        var depthOf = new Dictionary<int, int>();
        var boxes = new Dictionary<int, BBox>();
        BBox rootBox = ComputeSubtree(layout.All[0], 0, 1, radius, parentOf, depthOf, boxes);

        if (boxes.Count != layout.All.Count)
        {
            report.Errors.Add($"bbox count {boxes.Count} != visible {layout.All.Count}");
        }

        report.MaxDepth = depthOf.Count == 0 ? 0 : depthOf.Values.Max();

        // 父子几何 + 兄弟子树间距
        foreach (TreeLayout.Entry entry in layout.All)
        {
            if (entry.Children.Count == 0)
            {
                continue;
            }

            float expectedY = entry.Y + TreeLayout.LevelHeight;
            if (Math.Abs(entry.Children[0].X - entry.X) > Eps)
            {
                report.Errors.Add(
                    $"first child #{entry.Children[0].Id} X {entry.Children[0].X:0.###} != parent #{entry.Id} X {entry.X:0.###}");
            }

            for (int i = 0; i < entry.Children.Count; i++)
            {
                TreeLayout.Entry child = entry.Children[i];
                if (Math.Abs(child.Y - expectedY) > Eps)
                {
                    report.Errors.Add($"child #{child.Id} Y {child.Y:0.###} != {expectedY:0.###}");
                }

                if (i > 0 && child.X <= entry.Children[i - 1].X + Eps)
                {
                    report.Errors.Add(
                        $"children of #{entry.Id} not left-to-right: #{entry.Children[i - 1].Id} X {entry.Children[i - 1].X:0.###}, #{child.Id} X {child.X:0.###}");
                }

                if (child.View.ParentId != entry.Id)
                {
                    report.Errors.Add($"child #{child.Id} ParentId {child.View.ParentId} != {entry.Id}");
                }

                if (!entry.View.Children.Contains(child.Id))
                {
                    report.Errors.Add($"child #{child.Id} not in parent's Children array");
                }
            }

            for (int i = 1; i < entry.Children.Count; i++)
            {
                TreeLayout.Entry previous = entry.Children[i - 1];
                TreeLayout.Entry current = entry.Children[i];
                if (!boxes.TryGetValue(previous.Id, out BBox prevBox) ||
                    !boxes.TryGetValue(current.Id, out BBox currentBox))
                {
                    continue;
                }

                double gap = currentBox.MinX - prevBox.MaxX;
                double deviation = Math.Abs(gap - TreeLayout.SiblingGap);
                report.MaxSiblingGapDeviation = Math.Max(report.MaxSiblingGapDeviation, deviation);

                if (deviation > Eps)
                {
                    report.Errors.Add(
                        $"sibling subtree gap under #{entry.Id}: #{previous.Id}->#{current.Id} is {gap:0.###}, expected {TreeLayout.SiblingGap:0.###}");
                }
            }
        }

        // 同层节点间距（圆心距至少 直径 + 间隙）
        var byLevel = layout.All
            .GroupBy(e => Math.Round(e.Y, 3))
            .OrderBy(g => g.Key);

        foreach (IGrouping<double, TreeLayout.Entry> level in byLevel)
        {
            TreeLayout.Entry[] row = level.OrderBy(e => e.X).ToArray();
            for (int i = 1; i < row.Length; i++)
            {
                double gap = row[i].X - row[i - 1].X;
                report.MinSameLevelGap = Math.Min(report.MinSameLevelGap, gap);
                double minGap = TreeLayout.NodeDiameter + TreeLayout.SiblingGap;
                if (gap < minGap - Eps)
                {
                    report.Errors.Add(
                        $"same-level nodes #{row[i - 1].Id} and #{row[i].Id} too close: center distance {gap:0.###}, expected >= {minGap:0.###}");
                }
            }
        }

        // 任意两棵互不包含的子树：Y 区间相交时，X 必须留出间隙
        for (int i = 0; i < layout.All.Count; i++)
        {
            TreeLayout.Entry a = layout.All[i];
            BBox boxA = boxes[a.Id];
            for (int j = i + 1; j < layout.All.Count; j++)
            {
                TreeLayout.Entry b = layout.All[j];
                if (IsAncestor(a.Id, b.Id, parentOf) || IsAncestor(b.Id, a.Id, parentOf))
                {
                    continue;
                }

                BBox boxB = boxes[b.Id];
                if (!boxA.OverlapsY(boxB, Eps))
                {
                    continue;
                }

                report.SubtreePairsChecked++;
                bool separated = boxA.MaxX + TreeLayout.SiblingGap <= boxB.MinX + Eps
                                 || boxB.MaxX + TreeLayout.SiblingGap <= boxA.MinX + Eps;
                if (!separated)
                {
                    report.Errors.Add(
                        $"subtree bbox collision: (#{a.Id} bbox {boxA.MinX:0.##}..{boxA.MaxX:0.##} x {boxA.MinY:0.##}..{boxA.MaxY:0.##}) vs " +
                        $"(#{b.Id} bbox {boxB.MinX:0.##}..{boxB.MaxX:0.##} x {boxB.MinY:0.##}..{boxB.MaxY:0.##})");
                }
            }
        }

        // 连线不能穿过任何节点的圆（含自身端点的圆，距离应恰好 = r）
        foreach (TreeLayout.Entry entry in layout.All)
        {
            if (entry.Children.Count == 0)
            {
                continue;
            }

            float parentBottom = entry.Y + radius;
            float busY = entry.Y + TreeLayout.LevelHeight * 0.5f;
            float previousX = entry.X;

            for (int i = 0; i < entry.Children.Count; i++)
            {
                TreeLayout.Entry child = entry.Children[i];
                float childTop = child.Y - radius;

                if (i == 0)
                {
                    CheckSegment(report, layout.All, radius, entry, child,
                        entry.X, parentBottom, child.X, childTop);
                }
                else
                {
                    CheckSegment(report, layout.All, radius, entry, child,
                        previousX, busY, child.X, busY);
                    CheckSegment(report, layout.All, radius, entry, child,
                        child.X, busY, child.X, childTop);
                }

                previousX = child.X;
            }
        }

        // 画布尺寸不能留额外空白
        double expectedWidth = rootBox.MaxX - rootBox.MinX + TreeLayout.Padding * 2f;
        double expectedHeight = rootBox.MaxY - rootBox.MinY + TreeLayout.Padding * 2f;
        if (Math.Abs(layout.Width - expectedWidth) > Eps)
        {
            report.Errors.Add($"canvas width {layout.Width:0.###} != bbox width {expectedWidth:0.###} + padding");
        }

        if (Math.Abs(layout.Height - expectedHeight) > Eps)
        {
            report.Errors.Add($"canvas height {layout.Height:0.###} != bbox height {expectedHeight:0.###} + padding");
        }

        if (Math.Abs(rootBox.MinX - TreeLayout.Padding) > Eps)
        {
            report.Errors.Add($"left padding {rootBox.MinX:0.###} != {TreeLayout.Padding:0.###}");
        }

        foreach (TreeLayout.Entry entry in layout.All)
        {
            if (entry.X < -Eps || entry.X > layout.Width + Eps ||
                entry.Y < -Eps || entry.Y > layout.Height + Eps)
            {
                report.Errors.Add($"node #{entry.Id} outside canvas: ({entry.X:0.##},{entry.Y:0.##}) / {layout.Width:0.##}x{layout.Height:0.##}");
            }
        }

        // 折叠一致性
        foreach (TreeLayout.Entry entry in layout.All)
        {
            TestNode node = test.Get(entry.Id);
            if (entry.Collapsed)
            {
                if (node == null || node.Children.Count == 0)
                {
                    report.Errors.Add($"node #{entry.Id} marked collapsed but has no children");
                }

                if (entry.Children.Count != 0)
                {
                    report.Errors.Add($"node #{entry.Id} marked collapsed but still has visible children");
                }

                if (!test.Collapsed.Contains(entry.Id))
                {
                    report.Errors.Add($"node #{entry.Id} is collapsed in layout but not in collapse set");
                }
            }
            else
            {
                if (test.Collapsed.Contains(entry.Id) && node != null && node.Children.Count > 0)
                {
                    report.Errors.Add($"node #{entry.Id} is in collapse set but rendered expanded");
                }

                if (node != null && entry.Children.Count != node.Children.Count)
                {
                    report.Errors.Add(
                        $"node #{entry.Id} visible children {entry.Children.Count} != tree children {node.Children.Count}");
                }
            }
        }

        if (report.Errors.Count > 0)
        {
            report.Errors.Insert(0, $"step {step}: {operation} | nodes={test.Count} collapsed={test.Collapsed.Count}");
        }

        return report;
    }

    /// <summary>同一份数据重复构建，坐标必须完全一致（实时刷新不应抖动）。</summary>
    public static void CheckDeterminism(TreeLayout first, TreeLayout second, ValidationReport report)
    {
        if (first.All.Count != second.All.Count)
        {
            report.Errors.Add($"determinism: node count {first.All.Count} != {second.All.Count}");
            return;
        }

        if (Math.Abs(first.Width - second.Width) > Eps || Math.Abs(first.Height - second.Height) > Eps)
        {
            report.Errors.Add($"determinism: canvas {first.Width}x{first.Height} != {second.Width}x{second.Height}");
        }

        for (int i = 0; i < first.All.Count; i++)
        {
            TreeLayout.Entry a = first.All[i];
            TreeLayout.Entry b = second.All[i];
            if (a.Id != b.Id || Math.Abs(a.X - b.X) > Eps || Math.Abs(a.Y - b.Y) > Eps ||
                a.Collapsed != b.Collapsed)
            {
                report.Errors.Add($"determinism: entry {i} #{a.Id} differs");
                return;
            }
        }
    }

    private static void CollectExpectedVisible(RandomTreeCase test, int id, HashSet<int> into)
    {
        TestNode node = test.Get(id);
        if (node == null)
        {
            return;
        }

        into.Add(id);
        if (node.Children.Count > 0 && test.Collapsed.Contains(id))
        {
            return;
        }

        foreach (int child in node.Children)
        {
            CollectExpectedVisible(test, child, into);
        }
    }

    private static BBox ComputeSubtree(TreeLayout.Entry entry, int parentId, int depth, float radius,
        Dictionary<int, int> parentOf, Dictionary<int, int> depthOf, Dictionary<int, BBox> boxes)
    {
        parentOf[entry.Id] = parentId;
        depthOf[entry.Id] = depth;

        float minX = entry.X - radius;
        float maxX = entry.X + radius;
        float minY = entry.Y - radius;
        float maxY = entry.Y + radius;

        foreach (TreeLayout.Entry child in entry.Children)
        {
            BBox childBox = ComputeSubtree(child, entry.Id, depth + 1, radius, parentOf, depthOf, boxes);
            minX = Math.Min(minX, childBox.MinX);
            maxX = Math.Max(maxX, childBox.MaxX);
            minY = Math.Min(minY, childBox.MinY);
            maxY = Math.Max(maxY, childBox.MaxY);
        }

        var box = new BBox(minX, maxX, minY, maxY);
        boxes[entry.Id] = box;
        return box;
    }

    private static bool IsAncestor(int ancestorId, int id, Dictionary<int, int> parentOf)
    {
        int guard = 0;
        int current = id;
        while (current != 0 && guard++ < 100000)
        {
            if (current == ancestorId)
            {
                return true;
            }

            if (!parentOf.TryGetValue(current, out current))
            {
                return false;
            }
        }

        return false;
    }

    private static void CheckSegment(ValidationReport report, List<TreeLayout.Entry> entries, float radius,
        TreeLayout.Entry parent, TreeLayout.Entry child, float x1, float y1, float x2, float y2)
    {
        foreach (TreeLayout.Entry entry in entries)
        {
            double distance = DistanceToSegment(entry.X, entry.Y, x1, y1, x2, y2);
            report.EdgeNodePairsChecked++;
            if (distance < report.MinEdgeNodeDistance)
            {
                report.MinEdgeNodeDistance = distance;
            }

            if (distance < radius - 0.01f)
            {
                report.Errors.Add(
                    $"edge #{parent.Id}->#{child.Id} passes through node #{entry.Id}: distance {distance:0.###} < radius {radius:0.###}");
            }
        }
    }

    private static double DistanceToSegment(float px, float py, float x1, float y1, float x2, float y2)
    {
        double dx = x2 - x1;
        double dy = y2 - y1;
        double lengthSq = dx * dx + dy * dy;
        if (lengthSq <= 1e-9)
        {
            return Math.Sqrt((px - x1) * (px - x1) + (py - y1) * (py - y1));
        }

        double t = ((px - x1) * dx + (py - y1) * dy) / lengthSq;
        t = Math.Max(0, Math.Min(1, t));
        double cx = x1 + t * dx;
        double cy = y1 + t * dy;
        return Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    public static string FormatErrors(ValidationReport report, int max = 12)
    {
        var builder = new StringBuilder();
        for (int i = 0; i < report.Errors.Count && i < max; i++)
        {
            builder.AppendLine("  - " + report.Errors[i]);
        }

        if (report.Errors.Count > max)
        {
            builder.AppendLine($"  ... {report.Errors.Count - max} more");
        }

        return builder.ToString();
    }
}
