using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Runs;
using GlobalRewind.Players;
using GlobalRewind.Presentation;
using GlobalRewind.Tree;

namespace GlobalRewind.App;

/// <summary>
/// 逻辑层 → UI 的适配器：实现 <see cref="ITreeOverlaySource"/>。
///
/// 职责边界：
///   · 这是**唯一**把 RewindTree / RewindRuntime 翻译成 UI 数据的地方
///     （标签、副标题、tooltip、空状态文案都在这里格式化）；
///   · UI 的"节点左键/右键"事件在这里映射成跳转 / 剪枝；
///   · 逻辑层的 Changed / SessionEnded 事件转发成 UI 契约事件。
///
/// UI 层（Presentation）不认识本类，也不认识 RewindRuntime。
/// </summary>
internal sealed class RewindUiSource : ITreeOverlaySource
{
    private const string Hint =
        "左键跳转 · 右键剪枝 · 点击连线折叠/展开 · 拖拽移动 · 右下角调大小 · Ctrl+滚轮缩放";

    private readonly RewindRuntime _runtime;

    public RewindUiSource(RewindRuntime runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _runtime.Changed += OnRuntimeChanged;
        _runtime.SessionEnded += OnSessionEnded;

        RewindHud.NodeActivated += OnNodeActivated;
        RewindHud.NodeContextActivated += OnNodeContextActivated;
    }

    public event Action Changed;

    public event Action HideRequested;

    public bool CanShow
    {
        get
        {
            try
            {
                return NGame.Instance != null
                       && RunManager.Instance != null
                       && RunManager.Instance.IsInProgress;
            }
            catch
            {
                return false;
            }
        }
    }

    public TreeOverlaySnapshot Capture()
    {
        RewindTree tree = _runtime.Tree;
        var nodes = new List<TreeNodeView>(tree?.Count ?? 0);

        if (tree?.Root != null)
        {
            AddNode(tree, tree.Root.Id, 0, nodes);
        }

        // 第二遍：统计每个节点被多少个传送门指向，并生成"传送门 → 规范节点"的连线。
        var byId = new Dictionary<int, TreeNodeView>(nodes.Count);
        foreach (TreeNodeView view in nodes)
        {
            byId[view.Id] = view;
        }

        var links = new List<TreeLinkView>();
        foreach (TreeNodeView view in nodes)
        {
            if (!view.IsPortal || !byId.TryGetValue(view.PortalTargetId, out TreeNodeView target))
            {
                continue;
            }

            target.PortalCount++;
            links.Add(new TreeLinkView
            {
                From = view.Id,
                To = view.PortalTargetId,
                Recent = view.Id == _runtime.LastPortalId,
                Details = BuildLinkDetails(tree, view.Id, target)
            });
        }

        RewindNode current = tree?.Current;
        int count = tree?.Count ?? 0;
        int portalCount = 0;
        foreach (TreeNodeView view in nodes)
        {
            if (view.IsPortal)
            {
                portalCount++;
            }
        }

        string mode = PlayerScope.IsMultiplayer
            ? "多人"
            : PlayerScope.IsFakeMultiplayer
                ? "假多人"
                : string.Empty;

        return new TreeOverlaySnapshot
        {
            Title = "回溯树",
            Subtitle = count == 0
                ? "本场战斗还没有节点"
                : $"{(mode.Length > 0 ? mode + " · " : string.Empty)}战斗 #{_runtime.CombatIndex} · "
                  + $"当前 #{current?.Id.ToString() ?? "-"} · {count} 个节点"
                  + (portalCount > 0 ? $" · {portalCount} 传送门" : string.Empty),
            Hint = Hint,
            EmptyText = "本场战斗还没有节点",
            FocusedId = tree?.CurrentId ?? 0,
            SessionKey = _runtime.CombatIndex,
            Nodes = nodes,
            Links = links
        };
    }

    /// <summary>
    /// 先序展开成扁平列表（子树连续），同时回填子树节点数。
    /// 缺失的节点直接跳过，不让坏数据把 UI 拖垮。
    /// </summary>
    private static int AddNode(RewindTree tree, int id, int parentId, List<TreeNodeView> into)
    {
        RewindNode node = tree.Get(id);
        if (node == null)
        {
            return 0;
        }

        var view = new TreeNodeView
        {
            Id = node.Id,
            ParentId = parentId,
            Children = node.Children.ToArray(),
            PortalTargetId = node.AliasOf,
            Label = node.AliasOf != 0 ? "≡" : node.Id.ToString(),
            Details = BuildDetails(tree, node, node.ParentId == 0)
        };

        into.Add(view);

        int subtreeSize = 1;
        foreach (int childId in node.Children)
        {
            subtreeSize += AddNode(tree, childId, node.Id, into);
        }

        view.SubtreeSize = subtreeSize;
        return subtreeSize;
    }

    private static string BuildDetails(RewindTree tree, RewindNode node, bool isRoot)
    {
        // 传送门不是状态：tooltip 必须把"我是谁、我要去哪"写死，否则排查时容易误判。
        if (node.AliasOf != 0)
        {
            RewindNode canonical = tree.Get(node.AliasOf);
            string action = string.IsNullOrEmpty(node.Label) ? KindName(node.Kind) : node.Label;
            string sameText = node.MergeExact ? "状态完全相同" : "可玩状态相同（差异只在日志/动作编号）";
            return $"#{node.Id} · 传送门\n{action}\n" +
                   $"≡ #{canonical?.Id ?? node.AliasOf}（{sameText}）\n" +
                   $"第 {node.Round} 轮 / 第 {node.Turn} 回合\n" +
                   $"校验和 {node.Checksum:x8}\n" +
                   "左键跳转到规范节点 · 右键删除这个传送门";
        }

        string title = isRoot ? $"#{node.Id} · 根节点" : $"#{node.Id} · {KindName(node.Kind)}";
        string children = node.Children.Count == 0 ? "叶子节点" : $"子节点 {node.Children.Count} 个";
        int portals = 0;
        foreach (int childId in node.Children)
        {
            if (tree.IsPortal(childId))
            {
                portals++;
            }
        }

        if (portals > 0)
        {
            children += $"（含 {portals} 传送门）";
        }

        string scope = PlayerScope.IsMultiplayerLike
            ? $"{(node.Shared ? "共享" : "本地")} · {(node.Playable ? "可跳转" : "仅记录")}\n"
            : string.Empty;

        string frontier = node.Frontier != null
            ? $"切面 {node.Frontier.Describe()}\n"
            : string.Empty;

        return $"{title}\n{node.Label}\n" +
               $"第 {node.Round} 轮 / 第 {node.Turn} 回合\n" +
               scope +
               frontier +
               $"校验和 {node.Checksum:x8}\n" +
               $"对象 {node.ObjectCount} · 字段 {node.FieldCount}\n" +
               $"{children}\n" +
               "左键跳转 · 右键剪枝";
    }

    private static string BuildLinkDetails(RewindTree tree, int portalId, TreeNodeView target)
    {
        RewindNode portal = tree.Get(portalId);
        string action = portal == null
            ? "这一步"
            : string.IsNullOrEmpty(portal.Label) ? KindName(portal.Kind) : portal.Label;
        uint checksum = portal?.Checksum ?? 0;

        string sameText = portal != null && !portal.MergeExact
            ? "可玩状态与"
            : "状态与";
        string tail = portal != null && !portal.MergeExact
            ? "\n等价同态：忽略战斗日志与动作编号"
            : string.Empty;
        return $"#{portalId} → #{target.Id}\n" +
               $"从 #{portal?.ParentId ?? 0} {action}，{sameText} #{target.Id} 相同\n" +
               $"校验和 {checksum:x8}{tail}\n" +
               "左键聚焦这条来路 · 右键删除这个传送门";
    }

    private static string KindName(string kind)
    {
        switch (kind)
        {
            case "combat_start": return "战斗开始";
            case "turn_start": return "回合开始";
            case "card": return "出牌";
            case "potion": return "用药水";
            case "potion_discard": return "丢弃药水";
            case "end_turn": return "结束回合";
            case "undo_end_turn": return "撤销结束回合";
            case "hook": return "遗物/能力结算";
            case "action": return "动作结算";
            case "test": return "手动抓点";
            default: return string.IsNullOrEmpty(kind) ? "状态" : kind;
        }
    }

    private void OnRuntimeChanged()
    {
        Changed?.Invoke();
    }

    private void OnSessionEnded()
    {
        HideRequested?.Invoke();
    }

    private void OnNodeActivated(int id)
    {
        RewindNode node = _runtime.Tree?.Get(id);
        int portalTarget = node?.AliasOf ?? 0;

        if (!_runtime.JumpTo(id, out string error))
        {
            RewindHud.ShowStatus(error, error: true);
            return;
        }

        RewindHud.ShowStatus(portalTarget != 0
            ? $"已跳转到 #{portalTarget}（传送门 #{id}）"
            : $"已跳转到 #{id}");
    }

    private void OnNodeContextActivated(int id)
    {
        bool portal = _runtime.Tree?.IsPortal(id) ?? false;

        if (!_runtime.Prune(id, out string error))
        {
            RewindHud.ShowStatus(error, error: true);
            return;
        }

        RewindHud.ShowStatus(_runtime.IsPrunePending
            ? $"已请求删除传送门 #{id}，等待其他玩家确认"
            : portal ? $"已删除传送门 #{id}" : $"已剪掉 #{id} 及其子树");
    }
}
