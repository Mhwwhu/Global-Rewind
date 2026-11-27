using System;
using System.Collections.Generic;
using GlobalRewind.Presentation;

namespace UiLayoutTest;

/// <summary>
/// 传送门连线的定向测试（不启动游戏）。
///
/// 覆盖四类几何：同层（下半层折线、进圆点底部）、目标更深、跨 3 棵子树、
/// 目标被折叠（收口到空心圆）。
/// 断言：从传送门底部出线、同层落在圆下缘且箭头朝上、其余落在目标上方边的中点、
/// 线段不穿节点圆、全在画布内、条数正确、两次构建完全一致。
/// </summary>
internal static class LinkScenarioTest
{
    public static bool Run(out string message)
    {
        var failures = new List<string>();

        CheckSameLevel(failures);
        CheckTargetDeeper(failures);
        CheckFarSpan(failures);
        CheckCollapsedTarget(failures);

        message = failures.Count == 0
            ? "4 scenarios passed (same-level / deeper / far-span / collapsed)"
            : string.Join(" | ", failures);
        return failures.Count == 0;
    }

    // ---------------------------------------------------------------- 场景

    private static void CheckSameLevel(List<string> failures)
    {
        // 1 的子节点 2（左支）与 3（右支）；6 在左支、4 在右支，同为第 2 层。
        TreeNodeView[] nodes =
        {
            Node(1, 0, 2, 3),
            Node(2, 1, 5, 6),
            Node(3, 1, 4),
            Leaf(4, 3),
            Leaf(5, 2),
            Portal(6, 2, 4)
        };

        TreeLayout layout = Build(nodes, Link(6, 4), out _, out _);
        Verify(layout, "same-level", 1, 4, failures);
    }

    private static void CheckTargetDeeper(List<string> failures)
    {
        // 8 在第 2 层，指向另一支第 4 层的 7：连线要往下走。
        TreeNodeView[] nodes =
        {
            Node(1, 0, 2, 3),
            Node(2, 1, 4, 5),
            Node(3, 1, 8),
            Node(4, 2, 6),
            Leaf(5, 2),
            Node(6, 4, 7),
            Leaf(7, 6),
            Portal(8, 3, 7)
        };

        TreeLayout layout = Build(nodes, Link(8, 7), out _, out _);
        Verify(layout, "deeper", 1, 7, failures);
    }

    private static void CheckFarSpan(List<string> failures)
    {
        // 四棵兄弟子树，最右边的传送门指回最左边第 3 层的节点：必须跨 3 棵子树。
        var nodes = new List<TreeNodeView>
        {
            Node(1, 0, 2, 3, 4, 5),
            Node(2, 1, 6), Node(6, 2, 7),
            Node(3, 1, 8), Node(8, 3, 9),
            Node(4, 1, 10), Node(10, 4, 11),
            Node(5, 1, 12), Node(12, 5, 13),
            Leaf(7, 6),
            Leaf(9, 8),
            Leaf(11, 10),
            Portal(13, 12, 7)
        };

        TreeLayout layout = Build(nodes.ToArray(), Link(13, 7), out float width, out float height);
        Verify(layout, "far-span", 1, 7, failures);

        if (layout.Links.Count > 0 && layout.Links[0].Points.Count > 5)
        {
            failures.Add($"far-span: 折线点过多（{layout.Links[0].Points.Count}），走线应保持直角简短");
        }

        if (width <= 0f || height <= 0f)
        {
            failures.Add("far-span: 画布尺寸异常");
        }
    }

    private static void CheckCollapsedTarget(List<string> failures)
    {
        // 折叠 3 之后，6 -> 4 的连线要收口到折叠区（3 的空心圆）上。
        TreeNodeView[] nodes =
        {
            Node(1, 0, 2, 3),
            Node(2, 1, 5, 6),
            Node(3, 1, 4),
            Leaf(4, 3),
            Leaf(5, 2),
            Portal(6, 2, 4)
        };

        var collapsed = new HashSet<int> { 3 };
        TreeLayout layout = TreeLayout.Build(
            new TreeOverlaySnapshot { Nodes = nodes, Links = new[] { Link(6, 4) }, SessionKey = 1 },
            collapsed);

        Verify(layout, "collapsed", 1, 3, failures);

        if (!layout.ById.ContainsKey(3))
        {
            failures.Add("collapsed: 折叠区节点 3 应该可见");
        }
    }

    // ---------------------------------------------------------------- 断言

    private static void Verify(TreeLayout layout, string name, int expectedLinks, int anchorId,
        List<string> failures)
    {
        if (layout.Links.Count != expectedLinks)
        {
            failures.Add($"{name}: 连线条数 {layout.Links.Count} != {expectedLinks}");
            return;
        }

        if (!layout.ById.TryGetValue(anchorId, out TreeLayout.Entry anchor))
        {
            failures.Add($"{name}: 找不到落点节点 #{anchorId}");
            return;
        }

        foreach (TreeLayout.LinkRoute route in layout.Links)
        {
            if (route.AnchorTo != anchorId)
            {
                failures.Add($"{name}: 落点记录 {route.AnchorTo} != {anchorId}");
            }

            // ① 从传送门**底部**出线：起点在源圆正下方。
            if (!layout.ById.TryGetValue(route.From, out TreeLayout.Entry source))
            {
                failures.Add($"{name}: 找不到源节点 #{route.From}");
                return;
            }

            TreeLayout.PointF start = route.Points[0];
            float expectedExit = source.Y + (source.View.IsPortal ? 11f : TreeLayout.NodeDiameter / 2f);
            if (Math.Abs(start.X - source.X) > 0.6f || Math.Abs(start.Y - expectedExit) > 0.6f)
            {
                failures.Add($"{name}: 出线点 ({start.X:0.0}, {start.Y:0.0}) 不在传送门底部");
            }

            // ② 第二点落在下一层空带中线。
            TreeLayout.PointF band = route.Points[1];
            if (Math.Abs(band.X - source.X) > 0.6f
                || Math.Abs(band.Y - (source.Y + TreeLayout.LevelHeight / 2f)) > 0.6f)
            {
                failures.Add($"{name}: 没有竖直落到空带中线 ({band.X:0.0}, {band.Y:0.0})");
            }

            // ③ 落点：同层进圆的下缘（箭头朝上），其余落在目标上方那条边的中点（箭头朝下）。
            bool sameLevel = Math.Abs(source.Y - anchor.Y) < 0.5f;
            TreeLayout.PointF end = route.Points[route.Points.Count - 1];

            if (sameLevel)
            {
                if (Math.Abs(end.X - anchor.X) > 0.6f
                    || Math.Abs(end.Y - (anchor.Y + TreeLayout.NodeDiameter / 2f)) > 0.6f)
                {
                    failures.Add($"{name}: 同层落点 ({end.X:0.0}, {end.Y:0.0}) 不在目标圆的下缘");
                }

                if (!route.ArrowUp)
                {
                    failures.Add($"{name}: 同层连线箭头应朝上");
                }

                if (Math.Abs(route.Points[2].Y - (source.Y + TreeLayout.LevelHeight / 2f)) > 0.6f)
                {
                    failures.Add($"{name}: 同层横段 {route.Points[2].Y:0.0} 不在下半层中线");
                }
            }
            else
            {
                if (Math.Abs(end.X - anchor.X) > 0.6f
                    || Math.Abs(end.Y - (anchor.Y - TreeLayout.LevelHeight / 2f)) > 0.6f)
                {
                    failures.Add($"{name}: 落点 ({end.X:0.0}, {end.Y:0.0}) 不在目标上方边的中点");
                }

                if (route.ArrowUp)
                {
                    failures.Add($"{name}: 非同层连线箭头应朝下");
                }
            }

            foreach (TreeLayout.PointF point in route.Points)
            {
                if (point.X < -0.5f || point.X > layout.Width + 0.5f
                    || point.Y < -0.5f || point.Y > layout.Height + 0.5f)
                {
                    failures.Add($"{name}: 折线点越界 ({point.X:0.0}, {point.Y:0.0})");
                    return;
                }
            }

            for (int i = 0; i < route.Points.Count - 1; i++)
            {
                TreeLayout.PointF a = route.Points[i];
                TreeLayout.PointF b = route.Points[i + 1];
                float length = (float)Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                int steps = Math.Max(2, (int)(length / 2f));

                for (int s = 0; s <= steps; s++)
                {
                    float t = (float)s / steps;
                    float x = a.X + (b.X - a.X) * t;
                    float y = a.Y + (b.Y - a.Y) * t;

                    foreach (TreeLayout.Entry entry in layout.All)
                    {
                        // 起点贴在源圆上，允许距离恰好等于半径。
                        if (s == 0 && i == 0 && entry.Id == source.Id)
                        {
                            continue;
                        }

                        float radius = entry.View.IsPortal ? 11f : TreeLayout.NodeDiameter / 2f;
                        float dx = x - entry.X;
                        float dy = y - entry.Y;
                        float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                        if (distance < radius - 0.6f)
                        {
                            failures.Add($"{name}: 连线穿过了 #{entry.Id}（距离 {distance:0.0} < {radius}）");
                            return;
                        }
                    }
                }
            }
        }
    }

    private static TreeLayout Build(TreeNodeView[] nodes, TreeLinkView link,
        out float width, out float height)
    {
        var snapshot = new TreeOverlaySnapshot
        {
            Nodes = nodes,
            Links = new[] { link },
            SessionKey = 1
        };

        TreeLayout first = TreeLayout.Build(snapshot, null);
        TreeLayout second = TreeLayout.Build(snapshot, null);

        width = first.Width;
        height = first.Height;

        if (first.Links.Count != second.Links.Count)
        {
            throw new InvalidOperationException("non-deterministic link count");
        }

        for (int i = 0; i < first.Links.Count; i++)
        {
            if (first.Links[i].Points.Count != second.Links[i].Points.Count)
            {
                throw new InvalidOperationException("non-deterministic link points");
            }

            for (int p = 0; p < first.Links[i].Points.Count; p++)
            {
                TreeLayout.PointF a = first.Links[i].Points[p];
                TreeLayout.PointF b = second.Links[i].Points[p];
                if (Math.Abs(a.X - b.X) > 0.01f || Math.Abs(a.Y - b.Y) > 0.01f)
                {
                    throw new InvalidOperationException("non-deterministic link geometry");
                }
            }
        }

        return first;
    }

    // ---------------------------------------------------------------- 构造

    private static TreeNodeView Node(int id, int parent, params int[] children)
    {
        return new TreeNodeView
        {
            Id = id,
            ParentId = parent,
            Children = children,
            Label = id.ToString(),
            Details = string.Empty,
            SubtreeSize = 1,
            PortalCount = 0
        };
    }

    private static TreeNodeView Leaf(int id, int parent)
    {
        return Node(id, parent);
    }

    private static TreeNodeView Portal(int id, int parent, int target)
    {
        return new TreeNodeView
        {
            Id = id,
            ParentId = parent,
            Children = Array.Empty<int>(),
            Label = "≡",
            PortalTargetId = target,
            Details = string.Empty,
            SubtreeSize = 1,
            PortalCount = 0
        };
    }

    private static TreeLinkView Link(int from, int to)
    {
        return new TreeLinkView { From = from, To = to, Details = $"{from}->{to}", Recent = false };
    }
}
