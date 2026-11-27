using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using GlobalRewind.Presentation;

namespace UiLayoutTest;

/// <summary>
/// 独立 UI 布局随机测试。
///
/// 不启动游戏、不加载 Godot：直接编译 Presentation 层真实的 TreeLayout/契约源码，
/// 用随机 create / prune / collapse / expand 序列把树推到 100+ 节点，
/// 每一步都检查：
///   · 可见节点集合与折叠状态一致；
///   · 第一子节点与父节点同 X，所有子节点同高、从左到右；
///   · 相邻兄弟子树的 bbox 间距恰好 = SiblingGap（既不碰撞，也不留多余空隙）；
///   · 同层任意两个节点圆心距 >= 直径 + SiblingGap；
///   · 任意两棵互不包含的子树在 Y 相交时 bbox 不重叠；
///   · 连线不穿过任何节点圆；
///   · 画布尺寸 = 根子树 bbox + padding（没有额外空白）；
///   · 同一份数据重复构建坐标完全一致。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        int runs = ArgInt(args, "--runs", 5);
        int initialNodes = ArgInt(args, "--nodes", 130);
        int steps = ArgInt(args, "--steps", 400);
        int baseSeed = ArgInt(args, "--seed", 20261126);
        int minNodes = ArgInt(args, "--min-nodes", 100);
        int maxNodes = ArgInt(args, "--max-nodes", initialNodes + 50);

        if (initialNodes < 100)
        {
            Console.Error.WriteLine("--nodes must be >= 100 (the test targets 100+ node trees)");
            return 2;
        }

        Console.WriteLine("== GlobalRewind UI layout randomized test ==");
        Console.WriteLine($"runs={runs} initialNodes={initialNodes} minNodes={minNodes} maxNodes={maxNodes} " +
                          $"steps={steps} baseSeed={baseSeed}");
        Console.WriteLine();

        if (!ValidatorSelfCheck.Run(out string selfCheckMessage))
        {
            Console.Error.WriteLine("validator self-check FAILED: " + selfCheckMessage);
            return 2;
        }

        Console.WriteLine($"validator self-check: {selfCheckMessage}");
        Console.WriteLine();

        if (!LinkScenarioTest.Run(out string linkMessage))
        {
            Console.Error.WriteLine("link scenarios FAILED: " + linkMessage);
            return 2;
        }

        Console.WriteLine($"link scenarios: {linkMessage}");
        Console.WriteLine();

        TreeShape[] shapes =
        {
            TreeShape.Wide,
            TreeShape.Deep,
            TreeShape.Balanced,
            TreeShape.Random,
            TreeShape.Random
        };

        var totalWatch = Stopwatch.StartNew();
        int passed = 0;
        int totalLayouts = 0;
        double globalMinSameGap = double.MaxValue;
        double globalMaxGapDeviation = 0;
        double globalMinEdgeDistance = double.MaxValue;

        for (int run = 1; run <= runs; run++)
        {
            int seed = baseSeed + run * 7919;
            TreeShape shape = shapes[(run - 1) % shapes.Length];
            var test = new RandomTreeCase(seed, initialNodes, minNodes, maxNodes, shape);
            var watch = Stopwatch.StartNew();

            TreeOverlaySnapshot snapshot = test.BuildSnapshot();
            TreeLayout layout = TreeLayout.Build(snapshot, test.Collapsed);
            ValidationReport initial = LayoutValidator.Validate(layout, test, snapshot, "initial", 0);
            LayoutValidator.CheckDeterminism(layout, TreeLayout.Build(snapshot, test.Collapsed), initial);
            totalLayouts++;

            if (!initial.Ok)
            {
                EnsureOutDir();
                string path = DumpFailure(run, 0, seed, test, layout, snapshot, "initial", initial);
                PrintFailure(run, seed, 0, "initial", initial, path);
                return 1;
            }

            int minSeen = test.Count;
            int maxSeen = test.Count;
            double runMinSameGap = initial.MinSameLevelGap;
            double runMaxGapDeviation = initial.MaxSiblingGapDeviation;
            double runMinEdgeDistance = initial.MinEdgeNodeDistance;
            int maxDepth = initial.MaxDepth;
            int maxChildren = initial.MaxChildren;
            float maxCanvasWidth = initial.CanvasWidth;
            float maxCanvasHeight = initial.CanvasHeight;

            for (int step = 1; step <= steps; step++)
            {
                string operation = test.ApplyRandomOperation();
                snapshot = test.BuildSnapshot();
                layout = TreeLayout.Build(snapshot, test.Collapsed);

                ValidationReport report = LayoutValidator.Validate(layout, test, snapshot, operation, step);
                LayoutValidator.CheckDeterminism(layout, TreeLayout.Build(snapshot, test.Collapsed), report);
                totalLayouts++;

                if (!report.Ok)
                {
                    EnsureOutDir();
                    string path = DumpFailure(run, step, seed, test, layout, snapshot, operation, report);
                    PrintFailure(run, seed, step, operation, report, path);
                    return 1;
                }

                minSeen = Math.Min(minSeen, test.Count);
                maxSeen = Math.Max(maxSeen, test.Count);
                runMinSameGap = Math.Min(runMinSameGap, report.MinSameLevelGap);
                runMaxGapDeviation = Math.Max(runMaxGapDeviation, report.MaxSiblingGapDeviation);
                runMinEdgeDistance = Math.Min(runMinEdgeDistance, report.MinEdgeNodeDistance);
                maxDepth = Math.Max(maxDepth, report.MaxDepth);
                maxChildren = Math.Max(maxChildren, report.MaxChildren);
                maxCanvasWidth = Math.Max(maxCanvasWidth, report.CanvasWidth);
                maxCanvasHeight = Math.Max(maxCanvasHeight, report.CanvasHeight);
            }

            watch.Stop();
            passed++;
            globalMinSameGap = Math.Min(globalMinSameGap, runMinSameGap);
            globalMaxGapDeviation = Math.Max(globalMaxGapDeviation, runMaxGapDeviation);
            globalMinEdgeDistance = Math.Min(globalMinEdgeDistance, runMinEdgeDistance);

            Console.WriteLine($"[run {run}] shape={shape} seed={seed} PASS  ({watch.ElapsedMilliseconds} ms)");
            Console.WriteLine($"  nodes: initial={initialNodes} min={minSeen} max={maxSeen}  maxDepth={maxDepth}  maxChildren={maxChildren}");
            Console.WriteLine($"  canvas: max {maxCanvasWidth:0} x {maxCanvasHeight:0}");
            Console.WriteLine($"  ops  : add={Op(test, "add")} prune={Op(test, "prune")} " +
                              $"collapse={Op(test, "collapse")} expand={Op(test, "expand")}");
            Console.WriteLine($"  ui   : layouts={steps + 1}  sameLevelGapMin={runMinSameGap:0.###}  " +
                              $"siblingGapDevMax={runMaxGapDeviation:0.######}  edgeNodeDistMin={runMinEdgeDistance:0.###}");
            Console.WriteLine();
        }

        totalWatch.Stop();
        Console.WriteLine($"ALL RUNS PASSED: {passed}/{runs} runs, {totalLayouts} layouts, {totalWatch.ElapsedMilliseconds} ms");
        Console.WriteLine($"global: sameLevelGapMin={globalMinSameGap:0.###} " +
                          $"siblingGapDevMax={globalMaxGapDeviation:0.######} edgeNodeDistMin={globalMinEdgeDistance:0.###}");
        return 0;
    }

    private static int Op(RandomTreeCase test, string name)
    {
        return test.OpCounts.TryGetValue(name, out int count) ? count : 0;
    }

    private static void PrintFailure(int run, int seed, int step, string operation,
        ValidationReport report, string path)
    {
        Console.WriteLine($"[run {run}] FAIL seed={seed} step={step} op={operation}");
        Console.WriteLine(LayoutValidator.FormatErrors(report));
        Console.WriteLine($"  details: {path}");
        Console.WriteLine($"  summary: {report.Errors.Count} error(s), {report.VisibleNodes} visible nodes, " +
                          $"subtreePairs={report.SubtreePairsChecked}, edgeNodePairs={report.EdgeNodePairsChecked}");
    }

    private static void EnsureOutDir()
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out"));
    }

    private static string DumpFailure(int run, int step, int seed, RandomTreeCase test,
        TreeLayout layout, TreeOverlaySnapshot snapshot, string operation, ValidationReport report)
    {
        string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "out"));
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"failure-run{run}-step{step}-seed{seed}.txt");

        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"run={run} step={step} seed={seed} shape={test.Shape} op={operation}");
        builder.AppendLine($"nodes={test.Count} collapsed={test.Collapsed.Count} visible={layout.All.Count}");
        builder.AppendLine();
        builder.AppendLine("ERRORS");
        foreach (string error in report.Errors)
        {
            builder.AppendLine("  - " + error);
        }

        builder.AppendLine();
        builder.AppendLine("OP LOG (tail)");
        int from = Math.Max(0, test.OpLog.Count - 40);
        for (int i = from; i < test.OpLog.Count; i++)
        {
            builder.AppendLine("  " + test.OpLog[i]);
        }

        builder.AppendLine();
        builder.AppendLine("TREE");
        builder.AppendLine(test.DumpTree());

        builder.AppendLine("LAYOUT");
        foreach (TreeLayout.Entry entry in layout.All)
        {
            builder.AppendLine(
                $"  #{entry.Id} x={entry.X:0.###} y={entry.Y:0.###} collapsed={entry.Collapsed} " +
                $"subtree={entry.SubtreeCount} children=[{string.Join(",", entry.Children.Select(c => c.Id))}]");
        }

        builder.AppendLine();
        builder.AppendLine($"canvas {layout.Width:0.###} x {layout.Height:0.###}");
        builder.AppendLine($"collapsed set: {string.Join(",", test.Collapsed.OrderBy(i => i))}");
        builder.AppendLine($"snapshot nodes: {snapshot.Nodes.Count}");

        File.WriteAllText(path, builder.ToString());
        return path;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name && int.TryParse(args[i + 1], out int value))
            {
                return value;
            }
        }

        return fallback;
    }
}
