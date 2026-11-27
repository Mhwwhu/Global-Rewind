using System;
using System.Collections.Generic;

namespace GlobalRewind.Presentation;

/// <summary>
/// 树浮层的数据源。UI 层只认识这个接口，不认识 RewindRuntime / RewindTree / 战斗语义。
///
/// 契约刻意做得很窄：
///   · 拉一份只读快照（纯数据，不含任何 Godot 对象）；
///   · 逻辑层状态变化时发 <see cref="Changed"/>；
///   · 逻辑层要求收起浮层时发 <see cref="HideRequested"/>（战斗结束 / 整局结束）；
///   · <see cref="CanShow"/> 决定"现在能不能召出"（由逻辑层判断是否在局内）。
///
/// 事件回调（节点左键 / 右键）走 <see cref="RewindHud"/> 自己的事件，UI 不知道
/// "左键" 代表跳转还是别的什么 —— 语义完全在 App 层的适配器里。
/// </summary>
internal interface ITreeOverlaySource
{
    event Action Changed;

    event Action HideRequested;

    bool CanShow { get; }

    TreeOverlaySnapshot Capture();
}

/// <summary>
/// 一帧只读的树浮层快照。字段都是"可直接显示"的数据：
/// 标题、副标题、提示语、节点标签与详情、焦点节点。
/// </summary>
internal sealed class TreeOverlaySnapshot
{
    public string Title = "回溯树";
    public string Subtitle = string.Empty;
    public string Hint = string.Empty;
    public string EmptyText = "没有节点";

    /// <summary>画外圈高亮的节点；0 表示没有。</summary>
    public int FocusedId;

    /// <summary>用于在换战斗时重置折叠状态，UI 不关心它具体是什么。</summary>
    public int SessionKey;

    public IReadOnlyList<TreeNodeView> Nodes = Array.Empty<TreeNodeView>();

    /// <summary>传送门 → 规范节点的连线（折叠后的可见子集由布局层筛）。</summary>
    public IReadOnlyList<TreeLinkView> Links = Array.Empty<TreeLinkView>();

    public static TreeOverlaySnapshot Empty { get; } = new TreeOverlaySnapshot();
}

/// <summary>
/// 树上的一个节点。UI 只把它当作"有编号/标签、有父子关系、有详情文本"的普通节点。
/// </summary>
internal sealed class TreeNodeView
{
    public int Id;
    public int ParentId;
    public int[] Children = Array.Empty<int>();

    /// <summary>圆点里显示的文字（当前就是编号）。</summary>
    public string Label = string.Empty;

    /// <summary>悬停时显示的多行详情，已经由逻辑层格式化好。</summary>
    public string Details = string.Empty;

    /// <summary>真实子树节点数（含自身），折叠时用于显示"还有多少节点"。</summary>
    public int SubtreeSize = 1;

    /// <summary>
    /// 传送门指向的规范节点 id；0 表示普通节点。
    /// 传送门是叶子，点击等同点击规范节点。
    /// </summary>
    public int PortalTargetId;

    public bool IsPortal => PortalTargetId != 0;

    /// <summary>被多少个传送门指向。&gt;0 的规范节点外圈画一道细白环。</summary>
    public int PortalCount;
}

/// <summary>
/// 一条"传送门 → 规范节点"的虚线。UI 只负责画，`From` 一定是可见的传送门，
/// `To` 可能被折叠隐藏（此时布局层会把线收口到折叠区的空心圆上）。
/// </summary>
internal sealed class TreeLinkView
{
    public int From;
    public int To;

    /// <summary>已格式化好的多行 tooltip。</summary>
    public string Details = string.Empty;

    /// <summary>最近一次合并产生的传送门（画粗一点）。</summary>
    public bool Recent;
}
