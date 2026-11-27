using System;
using System.Collections.Generic;
using Godot;

namespace GlobalRewind.Presentation;

/// <summary>
/// 把 <see cref="TreeLayout"/> 画成 Godot 节点。这是一个**通用树视图**：
/// 只认识节点标签、父子关系、详情文本，不认识"跳转""剪枝""checksum"等任何领域语义。
/// 左键/右键只是发 <c>onActivate</c> / <c>onContext</c> 回调，由上层决定含义。
///
/// 只用内置节点（Button / ColorRect / Panel）：运行时创建的 C# 节点收不到
/// `_Draw` / `_GuiInput` 虚方法回调，所以不用自绘 Control。
///
/// 交互：
///   · 编号圆点：左键 onActivate，右键 onContext，悬停看详情；
///   · 连线：左键折叠/展开该子节点的整棵子树；
///   · 折叠后显示为空心圆：不显示编号，悬停显示节点数，左键展开。
/// </summary>
internal static class RewindGraphView
{
    private static readonly Color[] BranchColors =
    {
        new Color(0.42f, 0.72f, 1.00f),
        new Color(0.50f, 0.86f, 0.64f),
        new Color(0.98f, 0.74f, 0.42f),
        new Color(0.96f, 0.56f, 0.62f),
        new Color(0.73f, 0.63f, 0.98f),
        new Color(0.42f, 0.84f, 0.82f)
    };

    private static readonly Color AccentColor = new Color(1.00f, 0.82f, 0.46f);
    private static readonly Color TextColor = new Color(0.93f, 0.95f, 0.99f);

    public static void Populate(Control canvas, TreeLayout layout, float zoom, int focusedId,
        string emptyText, Action<int> onActivate, Action<int> onContext, Action<int> onToggleCollapse,
        Action<int, int> onLinkToggle, int focusLinkFrom, int focusLinkTo)
    {
        foreach (Node child in canvas.GetChildren())
        {
            canvas.RemoveChild(child);
            child.QueueFree();
        }

        float unit = Mathf.Max(0.2f, zoom);
        float radius = TreeLayout.NodeDiameter / 2f * unit;
        float diameter = radius * 2f;
        float lineWidth = Mathf.Max(1f, 2f * unit);

        canvas.CustomMinimumSize = layout.IsEmpty
            ? new Vector2(640, 240)
            : new Vector2(
                Mathf.Max(1f, layout.Width * unit),
                Mathf.Max(1f, layout.Height * unit));
        canvas.Size = canvas.CustomMinimumSize;

        if (layout.IsEmpty)
        {
            var empty = new Label
            {
                Text = string.IsNullOrEmpty(emptyText) ? "没有节点" : emptyText,
                Position = new Vector2(24, 24),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            empty.AddThemeFontSizeOverride("font_size", 13);
            empty.AddThemeColorOverride("font_color", new Color(0.66f, 0.70f, 0.78f));
            canvas.AddChild(empty);
            return;
        }

        int rootId = layout.RootId;

        foreach (TreeLayout.Entry entry in layout.All)
        {
            DrawEdges(canvas, entry, unit, radius, lineWidth, onToggleCollapse);
        }

        // 连线画在节点下面：命中区不会挡住圆点，视觉上也更像"从底下穿过"。
        DrawLinks(canvas, layout, unit, focusLinkFrom, focusLinkTo, onLinkToggle, onContext);

        foreach (TreeLayout.Entry entry in layout.All)
        {
            DrawNode(canvas, layout, entry, rootId, focusedId, unit, diameter,
                onActivate, onContext, onToggleCollapse);
        }
    }

    // ---------------------------------------------------------------- 连线

    private static void DrawEdges(Control canvas, TreeLayout.Entry parent,
        float unit, float radius, float lineWidth, Action<int> onToggleCollapse)
    {
        if (parent.Children.Count == 0)
        {
            return;
        }

        float parentX = parent.X * unit;
        float parentBottom = parent.Y * unit + radius;
        float busY = (parent.Y + TreeLayout.LevelHeight * 0.5f) * unit;
        float previousX = parentX;

        for (int i = 0; i < parent.Children.Count; i++)
        {
            TreeLayout.Entry child = parent.Children[i];
            float childX = child.X * unit;
            float childTop = child.Y * unit - radius;
            float segmentLeft = previousX;
            Color color = ColorOf(child);

            var segments = new List<ColorRect>(2);

            if (i == 0)
            {
                // 第一个子节点在正下方：父节点底部到子节点顶部是一条直线。
                segments.Add(AddSegment(canvas, new Rect2(
                    parentX - lineWidth / 2f,
                    parentBottom,
                    lineWidth,
                    Mathf.Max(1f, childTop - parentBottom)), color));
            }
            else
            {
                segments.Add(AddSegment(canvas, new Rect2(
                    segmentLeft,
                    busY - lineWidth / 2f,
                    Mathf.Max(1f, childX - segmentLeft),
                    lineWidth), color));

                segments.Add(AddSegment(canvas, new Rect2(
                    childX - lineWidth / 2f,
                    busY,
                    lineWidth,
                    Mathf.Max(1f, childTop - busY)), color));
            }

            previousX = childX;

            if (child.Children.Count == 0 && !child.Collapsed)
            {
                continue;
            }

            int childId = child.Id;
            string tooltip = child.Collapsed
                ? $"展开 #{child.Id} 的子树（{child.HiddenCount} 个节点）"
                : $"折叠 #{child.Id} 的子树（{child.SubtreeCount} 个节点）";

            if (i == 0)
            {
                AddEdgeHit(canvas, new Rect2(
                        parentX - 8f * unit,
                        parentBottom,
                        16f * unit,
                        Mathf.Max(2f, childTop - parentBottom)),
                    color, childId, tooltip, segments, onToggleCollapse);
            }
            else
            {
                AddEdgeHit(canvas, new Rect2(
                        segmentLeft,
                        busY - 8f * unit,
                        Mathf.Max(2f, childX - segmentLeft),
                        16f * unit),
                    color, childId, tooltip, segments, onToggleCollapse);

                AddEdgeHit(canvas, new Rect2(
                        childX - 8f * unit,
                        busY,
                        16f * unit,
                        Mathf.Max(2f, childTop - busY)),
                    color, childId, tooltip, segments, onToggleCollapse);
            }
        }
    }

    private static void AddEdgeHit(Control canvas, Rect2 rect, Color color, int childId,
        string tooltip, List<ColorRect> segments, Action<int> onToggleCollapse)
    {
        var hit = new Button
        {
            Flat = false,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            TooltipText = tooltip,
            Position = rect.Position,
            Size = new Vector2(Mathf.Max(2f, rect.Size.X), Mathf.Max(2f, rect.Size.Y))
        };
        hit.AddThemeStyleboxOverride("normal", TransparentBox());
        hit.AddThemeStyleboxOverride("hover", HighlightBox(color, 0.16f));
        hit.AddThemeStyleboxOverride("pressed", HighlightBox(color, 0.30f));
        hit.AddThemeStyleboxOverride("focus", TransparentBox());

        Color normal = WithAlpha(color, 0.42f);
        Color hot = WithAlpha(color, 0.95f);
        hit.MouseEntered += () => SetSegments(segments, hot);
        hit.MouseExited += () => SetSegments(segments, normal);
        hit.Pressed += () => onToggleCollapse(childId);

        canvas.AddChild(hit);
    }

    private static void SetSegments(List<ColorRect> segments, Color color)
    {
        foreach (ColorRect segment in segments)
        {
            if (GodotObject.IsInstanceValid(segment))
            {
                segment.Color = color;
            }
        }
    }

    private static ColorRect AddSegment(Control canvas, Rect2 rect, Color color)
    {
        var segment = new ColorRect
        {
            Color = WithAlpha(color, 0.42f),
            Position = rect.Position,
            Size = new Vector2(Mathf.Max(1f, rect.Size.X), Mathf.Max(1f, rect.Size.Y)),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        canvas.AddChild(segment);
        return segment;
    }

    // ---------------------------------------------------------------- 传送门连线

    private static void DrawLinks(Control canvas, TreeLayout layout, float unit,
        int focusFrom, int focusTo, Action<int, int> onLinkToggle, Action<int> onContext)
    {
        bool anyFocus = focusFrom != 0;

        foreach (TreeLayout.LinkRoute route in layout.Links)
        {
            if (route.Points.Count < 2)
            {
                continue;
            }

            bool focused = route.From == focusFrom && route.To == focusTo;
            float alpha = anyFocus ? (focused ? 0.95f : 0.12f) : 0.55f;
            Color color = layout.ById.TryGetValue(route.AnchorTo, out TreeLayout.Entry target)
                ? ColorOf(target)
                : AccentColor;

            var dashes = new List<ColorRect>();

            for (int i = 0; i < route.Points.Count - 1; i++)
            {
                TreeLayout.PointF a = route.Points[i];
                TreeLayout.PointF b = route.Points[i + 1];
                AddDashed(canvas, new Vector2(a.X * unit, a.Y * unit), new Vector2(b.X * unit, b.Y * unit),
                    color, alpha, unit, dashes);
            }

            // 落点在目标上方那条边的中点（箭头朝下）；同层连线落在圆的下缘（箭头朝上）。
            TreeLayout.PointF tip = route.Points[route.Points.Count - 1];
            AddChevron(canvas, new Vector2(tip.X * unit, tip.Y * unit), color,
                Mathf.Min(1f, alpha + 0.3f), unit, route.ArrowUp);

            // 每段一个命中区：悬停加亮、左键聚焦、右键删传送门。
            for (int i = 0; i < route.Points.Count - 1; i++)
            {
                TreeLayout.PointF a = route.Points[i];
                TreeLayout.PointF b = route.Points[i + 1];

                float x0 = Mathf.Min(a.X, b.X) * unit;
                float y0 = Mathf.Min(a.Y, b.Y) * unit;
                float w = Mathf.Max(6f, Mathf.Abs(a.X - b.X) * unit);
                float h = Mathf.Max(6f, Mathf.Abs(a.Y - b.Y) * unit);
                const float pad = 6f;

                var hit = new Button
                {
                    Flat = false,
                    FocusMode = Control.FocusModeEnum.None,
                    MouseFilter = Control.MouseFilterEnum.Stop,
                    MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                    TooltipText = route.Details,
                    Position = new Vector2(x0 - pad, y0 - pad),
                    Size = new Vector2(w + pad * 2f, h + pad * 2f)
                };

                hit.AddThemeStyleboxOverride("normal", TransparentBox());
                hit.AddThemeStyleboxOverride("hover", HighlightBox(color, 0.10f));
                hit.AddThemeStyleboxOverride("pressed", HighlightBox(color, 0.22f));
                hit.AddThemeStyleboxOverride("focus", TransparentBox());

                const float baseAlpha = 0.55f;
                float idleAlpha = anyFocus ? (focused ? 0.95f : 0.12f) : baseAlpha;
                hit.MouseEntered += () => SetSegments(dashes, WithAlpha(color, 1f));
                hit.MouseExited += () => SetSegments(dashes, WithAlpha(color, idleAlpha));

                int from = route.From;
                int to = route.To;
                hit.Pressed += () => onLinkToggle(from, to);
                hit.GuiInput += @event =>
                {
                    if (@event is InputEventMouseButton mouse
                        && mouse.Pressed
                        && mouse.ButtonIndex == MouseButton.Right)
                    {
                        hit.AcceptEvent();
                        onContext(from);
                    }
                };

                canvas.AddChild(hit);
            }
        }
    }

    private static void AddDashed(Control canvas, Vector2 a, Vector2 b, Color color, float alpha,
        float unit, List<ColorRect> into)
    {
        Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 0.5f)
        {
            return;
        }

        Vector2 dir = delta / length;
        float dash = Mathf.Max(3f, 6f * unit);
        float gap = Mathf.Max(2f, 5f * unit);
        float thickness = Mathf.Max(1f, 1.5f * unit);

        float t = 0f;
        while (t < length - 0.5f)
        {
            float seg = Mathf.Min(dash, length - t);
            Vector2 p0 = a + dir * t;
            Vector2 p1 = a + dir * (t + seg);

            var rect = new ColorRect
            {
                Color = WithAlpha(color, alpha),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };

            if (Mathf.Abs(dir.X) >= Mathf.Abs(dir.Y))
            {
                rect.Position = new Vector2(Mathf.Min(p0.X, p1.X), p0.Y - thickness / 2f);
                rect.Size = new Vector2(seg, thickness);
            }
            else
            {
                rect.Position = new Vector2(p0.X - thickness / 2f, Mathf.Min(p0.Y, p1.Y));
                rect.Size = new Vector2(thickness, seg);
            }

            canvas.AddChild(rect);
            into.Add(rect);
            t += dash + gap;
        }
    }

    private static void AddChevron(Control canvas, Vector2 tip, Color color, float alpha,
        float unit, bool up)
    {
        Color tint = WithAlpha(color, Mathf.Min(1f, alpha));
        float w = Mathf.Max(3f, 4.5f * unit);
        float h = Mathf.Max(4f, 6f * unit);
        float thickness = Mathf.Max(1f, 1.6f * unit);
        float sign = up ? 1f : -1f;

        AddLine(canvas, tip, tip + new Vector2(-w, sign * h), tint, thickness);
        AddLine(canvas, tip, tip + new Vector2(w, sign * h), tint, thickness);
    }

    /// <summary>任意方向的细线：用旋转后的 ColorRect 拼（内置节点里没有 Line2D 的替代）。</summary>
    private static void AddLine(Control canvas, Vector2 a, Vector2 b, Color color, float thickness)
    {
        Vector2 delta = b - a;
        float length = delta.Length();
        if (length < 0.5f)
        {
            return;
        }

        var rect = new ColorRect
        {
            Color = color,
            Position = a,
            Size = new Vector2(length, thickness),
            PivotOffset = new Vector2(0f, thickness / 2f),
            Rotation = Mathf.Atan2(delta.Y, delta.X),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        canvas.AddChild(rect);
    }

    // ---------------------------------------------------------------- 节点

    private static void DrawNode(Control canvas, TreeLayout layout, TreeLayout.Entry entry,
        int rootId, int focusedId, float unit, float diameter,
        Action<int> onActivate, Action<int> onContext, Action<int> onToggleCollapse)
    {
        bool isPortal = entry.View.IsPortal;
        float nodeDiameter = isPortal ? 22f * unit : diameter;
        float radius = nodeDiameter / 2f;
        float centerX = entry.X * unit;
        float centerY = entry.Y * unit;

        // 传送门用**目标分支**的颜色：线的归属比"我出生在哪"更有意义。
        Color color = ColorOf(entry);
        if (isPortal && layout.ById.TryGetValue(entry.View.PortalTargetId, out TreeLayout.Entry targetEntry))
        {
            color = ColorOf(targetEntry);
        }

        bool isRoot = entry.Id == rootId;
        bool isFocused = entry.Id == focusedId;

        // 被传送门指向的规范节点：外圈一道细白环（在焦点环下面）。
        if (!isPortal && entry.View.PortalCount > 0)
        {
            float portalRing = radius + 3.5f * unit;
            var pRing = new Panel
            {
                Position = new Vector2(centerX - portalRing, centerY - portalRing),
                Size = new Vector2(portalRing * 2f, portalRing * 2f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            var pStyle = new StyleBoxFlat
            {
                BgColor = new Color(0f, 0f, 0f, 0f),
                BorderColor = new Color(1f, 1f, 1f, 0.30f),
                DrawCenter = false
            };
            pStyle.SetBorderWidthAll(Mathf.Max(1, Mathf.RoundToInt(1.2f * unit)));
            pStyle.SetCornerRadiusAll(Mathf.Max(2, Mathf.RoundToInt(portalRing)));
            pRing.AddThemeStyleboxOverride("panel", pStyle);
            canvas.AddChild(pRing);
        }

        if (isFocused)
        {
            float ringRadius = radius + 5f * unit;
            var ring = new Panel
            {
                Position = new Vector2(centerX - ringRadius, centerY - ringRadius),
                Size = new Vector2(ringRadius * 2f, ringRadius * 2f),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            var ringStyle = new StyleBoxFlat
            {
                BgColor = new Color(0f, 0f, 0f, 0f),
                BorderColor = new Color(1f, 1f, 1f, 0.85f),
                DrawCenter = false
            };
            ringStyle.SetBorderWidthAll(Mathf.Max(1, Mathf.RoundToInt(2f * unit)));
            ringStyle.SetCornerRadiusAll(Mathf.Max(2, Mathf.RoundToInt(ringRadius)));
            ring.AddThemeStyleboxOverride("panel", ringStyle);
            canvas.AddChild(ring);
        }

        var button = new Button
        {
            Position = new Vector2(centerX - radius, centerY - radius),
            Size = new Vector2(nodeDiameter, nodeDiameter),
            CustomMinimumSize = new Vector2(nodeDiameter, nodeDiameter),
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            Text = isPortal ? "≡" : entry.Collapsed ? string.Empty : entry.View.Label,
            TooltipText = isPortal
                ? string.IsNullOrEmpty(entry.View.Details)
                    ? $"≡ #{entry.View.PortalTargetId}"
                    : entry.View.Details
                : entry.Collapsed
                    ? $"{entry.HiddenCount} 个节点\n点击展开"
                    : string.IsNullOrEmpty(entry.View.Details) ? entry.View.Label : entry.View.Details
        };

        int fontSize;
        if (isPortal)
        {
            fontSize = Mathf.Clamp(Mathf.RoundToInt(10f * unit), 7, 14);
        }
        else
        {
            fontSize = Mathf.RoundToInt(13f * unit);
            if (entry.Id >= 1000)
            {
                fontSize -= 2;
            }
            else if (entry.Id >= 100)
            {
                fontSize -= 1;
            }
        }

        int circleRadius = Mathf.Max(2, Mathf.RoundToInt(radius));

        button.AddThemeFontSizeOverride("font_size", Mathf.Clamp(fontSize, 7, 26));
        button.AddThemeColorOverride("font_color", TextColor);
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 1f, 1f));

        Color fill = isRoot ? AccentColor.Darkened(0.62f) : color.Darkened(0.60f);
        Color hoverFill = isRoot ? AccentColor.Darkened(0.45f) : color.Darkened(0.42f);
        Color pressedFill = isRoot ? AccentColor.Darkened(0.32f) : color.Darkened(0.30f);
        Color border = isRoot ? new Color(1f, 0.86f, 0.56f, 0.95f) : WithAlpha(color, isPortal ? 0.80f : 0.92f);
        int borderWidth = Mathf.Max(1, Mathf.RoundToInt((isPortal ? 1.4f : 1.5f) * unit));

        if (isPortal)
        {
            button.AddThemeStyleboxOverride("normal",
                HollowBox(border, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("hover",
                HollowHoverBox(border, color, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("pressed",
                HollowHoverBox(border, color, borderWidth, circleRadius));
        }
        else if (entry.Collapsed)
        {
            button.AddThemeStyleboxOverride("normal",
                HollowBox(border, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("hover",
                HollowHoverBox(border, color, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("pressed",
                HollowHoverBox(border, color, borderWidth, circleRadius));
        }
        else
        {
            button.AddThemeStyleboxOverride("normal",
                CircleBox(fill, border, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("hover",
                CircleBox(hoverFill, border, borderWidth, circleRadius));
            button.AddThemeStyleboxOverride("pressed",
                CircleBox(pressedFill, border, borderWidth, circleRadius));
        }

        button.AddThemeStyleboxOverride("focus", TransparentBox());

        int id = entry.Id;
        bool collapsed = entry.Collapsed;
        button.Pressed += () =>
        {
            if (isPortal)
            {
                onActivate(id);
            }
            else if (collapsed)
            {
                onToggleCollapse(id);
            }
            else if (id != focusedId)
            {
                onActivate(id);
            }
        };

        button.GuiInput += @event =>
        {
            if (@event is InputEventMouseButton mouse
                && mouse.Pressed
                && mouse.ButtonIndex == MouseButton.Right)
            {
                button.AcceptEvent();
                onContext(id);
            }
        };

        canvas.AddChild(button);
    }

    // ---------------------------------------------------------------- 样式

    private static Color ColorOf(TreeLayout.Entry entry)
    {
        if (entry.ColorIndex < 0)
        {
            return AccentColor;
        }

        return BranchColors[entry.ColorIndex % BranchColors.Length];
    }

    private static Color WithAlpha(Color color, float alpha)
    {
        return new Color(color.R, color.G, color.B, alpha);
    }

    private static StyleBoxFlat CircleBox(Color fill, Color border, int borderWidth, int radius)
    {
        var style = new StyleBoxFlat { BgColor = fill, BorderColor = border };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat HollowBox(Color border, int borderWidth, int radius)
    {
        var style = new StyleBoxFlat
        {
            BgColor = new Color(0f, 0f, 0f, 0f),
            BorderColor = border,
            DrawCenter = false
        };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat HollowHoverBox(Color border, Color tint, int borderWidth, int radius)
    {
        var style = new StyleBoxFlat
        {
            BgColor = WithAlpha(tint, 0.18f),
            BorderColor = border,
            DrawCenter = true
        };
        style.SetBorderWidthAll(borderWidth);
        style.SetCornerRadiusAll(radius);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat TransparentBox()
    {
        var style = new StyleBoxFlat { BgColor = new Color(0f, 0f, 0f, 0f) };
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(0);
        return style;
    }

    private static StyleBoxFlat HighlightBox(Color color, float alpha)
    {
        var style = new StyleBoxFlat
        {
            BgColor = WithAlpha(color, alpha),
            BorderColor = WithAlpha(color, alpha + 0.22f)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        style.SetContentMarginAll(0);
        return style;
    }
}
