using System;
using System.Collections.Generic;
using System.Linq;
using GlobalRewind.Presentation;

namespace UiLayoutTest;

/// <summary>
/// 校验器的自检：先证明它会对"碰撞"和"距离过远"报错，
/// 否则随机测试全绿也可能只是校验器什么都没查。
/// </summary>
internal static class ValidatorSelfCheck
{
    public static bool Run(out string message)
    {
        var snapshot = new TreeOverlaySnapshot
        {
            SessionKey = 1,
            Nodes = new List<TreeNodeView>
            {
                new TreeNodeView
                {
                    Id = 1, ParentId = 0, Children = new[] { 2, 3 },
                    Label = "1", Details = "#1", SubtreeSize = 3
                },
                new TreeNodeView
                {
                    Id = 2, ParentId = 1, Children = Array.Empty<int>(),
                    Label = "2", Details = "#2", SubtreeSize = 1
                },
                new TreeNodeView
                {
                    Id = 3, ParentId = 1, Children = Array.Empty<int>(),
                    Label = "3", Details = "#3", SubtreeSize = 1
                }
            }
        };

        var test = new RandomTreeCase(1, 1, 1, 1, TreeShape.Random);
        test.AddChild(1);
        test.AddChild(1);

        // 需要让 test 的节点数/父关系与 snapshot 一致，校验器的折叠/子节点数检查才不会被误伤。
        TreeLayout layout = TreeLayout.Build(snapshot, test.Collapsed);
        ValidationReport clean = LayoutValidator.Validate(layout, test, snapshot, "self-check-clean", 0);
        if (!clean.Ok)
        {
            message = "clean layout reported errors: " + string.Join(" | ", clean.Errors.Take(3));
            return false;
        }

        if (layout.All.Count < 3 || layout.All[0].Children.Count < 2)
        {
            message = "unexpected layout shape for self-check";
            return false;
        }

        // 折叠根节点：整棵树只剩一个空心圆，布局仍必须自洽。
        test.Collapsed.Add(1);
        TreeLayout rootOnly = TreeLayout.Build(snapshot, test.Collapsed);
        ValidationReport rootOnlyReport = LayoutValidator.Validate(rootOnly, test, snapshot, "self-check-root-collapsed", 0);
        test.Collapsed.Remove(1);
        if (!rootOnlyReport.Ok)
        {
            message = "root-collapsed layout reported errors: " +
                      string.Join(" | ", rootOnlyReport.Errors.Take(3));
            return false;
        }

        TreeLayout.Entry second = layout.All[0].Children[1];
        float originalX = second.X;

        // 1) 距离过远：兄弟子树间距从 30 变成 50
        second.X = originalX + 20;
        ValidationReport tooFar = LayoutValidator.Validate(layout, test, snapshot, "self-check-too-far", 0);
        if (!tooFar.Errors.Any(e => e.Contains("sibling subtree gap")))
        {
            message = "too-far mutation was not detected";
            return false;
        }

        // 2) 碰撞：兄弟子树间距从 30 变成 10
        second.X = originalX - 20;
        ValidationReport collision = LayoutValidator.Validate(layout, test, snapshot, "self-check-collision", 0);
        if (!collision.Errors.Any(e => e.Contains("sibling subtree gap") || e.Contains("too close")))
        {
            message = "collision mutation was not detected";
            return false;
        }

        second.X = originalX;
        message = $"OK (too-far {tooFar.Errors.Count} error(s), collision {collision.Errors.Count} error(s))";
        return true;
    }
}
