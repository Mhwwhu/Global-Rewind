using System;
using System.Collections.Generic;
using Godot;
using GlobalRewind.Logging;

namespace GlobalRewind.Presentation;

/// <summary>
/// 树浮层（纯 UI 壳）。
///
/// **不认识任何战斗/回溯语义**：数据只从 <see cref="ITreeOverlaySource"/> 拉，
/// 节点左键/右键只抛 <see cref="NodeActivated"/> / <see cref="NodeContextActivated"/> 事件，
/// 由上层（App 层）决定是跳转、剪枝还是别的动作。UI 只负责：
///   · 把快照画成圆点 + 连线，折叠子树，缩放/滚动/拖拽/半透明；
///   · 热键绑定设置与设置面板；
///   · 显示上层塞进来的副标题、提示语和状态消息。
///
/// 只用内置 Control 拼 UI：运行时创建的 C# 节点收不到 `_Draw` / `_GuiInput`
/// 虚方法回调，但原生信号（Pressed / GuiInput / MouseEntered）是可用的。
/// </summary>
internal static class RewindHud
{
    private enum BindTarget
    {
        None,
        Toggle,
        Prev,
        Next
    }

    private const int LayerIndex = 100;
    private const string FallbackHint = "左键打开 · 右键操作 · 点击连线折叠/展开 · 拖拽移动 · 右下角调大小 · Ctrl+滚轮缩放";
    private const float MinPanelWidth = 460f;
    private const float MinPanelHeight = 300f;
    private const float GripSize = 26f;

    private static readonly Color TextStrong = new Color(0.93f, 0.94f, 0.97f);
    private static readonly Color TextMuted = new Color(0.61f, 0.65f, 0.73f);
    private static readonly Color TextWarn = new Color(0.98f, 0.76f, 0.42f);
    private static readonly Color Accent = new Color(0.55f, 0.78f, 1.00f);

    private static ITreeOverlaySource _source;
    private static Func<Node> _hostProvider;
    private static TreeOverlaySnapshot _snapshot = TreeOverlaySnapshot.Empty;

    private static CanvasLayer _layer;
    private static Control _root;
    private static Control _frame;
    private static Panel _bg;
    private static PanelContainer _graphFrame;
    private static ScrollContainer _scroll;
    private static Control _canvas;
    private static Label _title;
    private static Label _subtitle;
    private static Label _status;
    private static Label _alphaLabel;
    private static Label _zoomLabel;
    private static HSlider _alphaSlider;
    private static PanelContainer _settingsPanel;
    private static Button _settingsButton;
    private static Button _closeButton;
    private static Control _grip;

    private static readonly List<BindRow> BindRows = new List<BindRow>();

    private static bool _installed;
    private static bool _open;
    private static bool _dragging;
    private static Vector2 _dragOffset;

    private static bool _resizing;
    private static Vector2 _resizeStartMouse;
    private static Vector2 _resizeStartSize;

    private static readonly HashSet<int> Collapsed = new HashSet<int>();
    private static int _collapsedSession = -1;
    private static int _lastFocusedId;

    /// <summary>被点击聚焦的传送门来路（0 = 没有聚焦）。</summary>
    private static int _focusLinkFrom;
    private static int _focusLinkTo;

    private static TreeLayout _layout;
    private static float _zoom = 1f;

    private static bool _pendingScroll;
    private static int _pendingScrollX;
    private static int _pendingScrollY;

    private static BindTarget _binding = BindTarget.None;
    private static ulong _statusUntil;

    private sealed class BindRow
    {
        public BindTarget Target;
        public Button Button;
    }

    /// <summary>树节点被左键激活。语义由上层决定（本模组里是跳转）。</summary>
    public static event Action<int> NodeActivated;

    /// <summary>树节点被右键激活。语义由上层决定（本模组里是剪枝）。</summary>
    public static event Action<int> NodeContextActivated;

    /// <summary>键位被修改/恢复默认。上层据此重置输入轮询状态（UI 不直接碰输入层）。</summary>
    public static event Action HotkeysChanged;

    public static bool IsOpen => _open;

    public static bool IsCapturingKey => _binding != BindTarget.None;

    private static bool CanShow => _source != null && _source.CanShow;

    // ---------------------------------------------------------------- 生命周期

    /// <summary>
    /// 注入数据源并开始监听。UI 与逻辑层的唯一连接点。
    /// </summary>
    public static void Initialize(ITreeOverlaySource source, Func<Node> hostProvider)
    {
        _hostProvider = hostProvider ?? throw new ArgumentNullException(nameof(hostProvider));

        if (ReferenceEquals(_source, source))
        {
            return;
        }

        if (_source != null)
        {
            _source.Changed -= OnSourceChanged;
            _source.HideRequested -= OnSourceHideRequested;
        }

        _source = source;

        if (_source != null)
        {
            _source.Changed += OnSourceChanged;
            _source.HideRequested += OnSourceHideRequested;
        }

        Install();
    }

    private static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        if (Engine.GetMainLoop() is SceneTree tree)
        {
            tree.ProcessFrame += OnProcessFrame;
        }
    }

    private static void OnSourceChanged()
    {
        if (!_open)
        {
            return;
        }

        if (!CanShow)
        {
            Close();
            return;
        }

        Refresh(centerFocused: false);
    }

    private static void OnSourceHideRequested()
    {
        Close();
    }

    public static void Toggle()
    {
        if (_open)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    public static void Open()
    {
        try
        {
            if (!CanShow)
            {
                ModLog.Info("hud open ignored: source unavailable");
                return;
            }

            EnsureBuilt();
            if (_frame == null || !GodotObject.IsInstanceValid(_frame))
            {
                return;
            }

            TreeOverlaySnapshot snapshot = _source.Capture();
            if (snapshot.SessionKey != _collapsedSession)
            {
                Collapsed.Clear();
                _collapsedSession = snapshot.SessionKey;
            }

            _open = true;
            _binding = BindTarget.None;
            _statusUntil = 0;

            // 每次召出都保证焦点节点可见（祖先折叠会遮住它）；里层折叠状态不动。
            _lastFocusedId = 0;

            if (_settingsPanel != null && GodotObject.IsInstanceValid(_settingsPanel))
            {
                _settingsPanel.Visible = false;
            }

            _layer.Visible = true;
            _frame.Visible = true;

            PlacePanel();
            Refresh(centerFocused: true);
            SetStatus($"按 {RewindSettings.Instance.ToggleKey.Display} 关闭", 3000);

            ModLog.Info("hud opened");
        }
        catch (Exception e)
        {
            ModLog.Warn($"open hud failed: {e.Message}");
        }
    }

    public static void Close()
    {
        bool wasOpen = _open;

        try
        {
            _open = false;
            _dragging = false;
            _resizing = false;
            _binding = BindTarget.None;
            _pendingScroll = false;

            if (_settingsPanel != null && GodotObject.IsInstanceValid(_settingsPanel))
            {
                _settingsPanel.Visible = false;
            }

            if (_frame != null && GodotObject.IsInstanceValid(_frame))
            {
                if (wasOpen)
                {
                    ClampPanelToViewport();
                }

                _frame.Visible = false;
            }

            if (_layer != null && GodotObject.IsInstanceValid(_layer))
            {
                _layer.Visible = false;
            }

            if (wasOpen)
            {
                RewindSettings.Instance.Save();
            }
        }
        catch
        {
            // 场景已销毁时忽略
        }
    }

    /// <summary>
    /// 上层显示一条状态消息；浮层没开就先打开。消息内容对 UI 是不透明的。
    /// </summary>
    public static void ShowStatus(string message, bool error = false)
    {
        if (!CanShow)
        {
            ModLog.Warn($"hud status ignored (not in game): {message}");
            return;
        }

        if (!_open)
        {
            Open();
        }

        SetStatus(message, error ? 8000UL : 4000UL, error);
    }

    // ---------------------------------------------------------------- 键位绑定

    /// <summary>
    /// 由输入通路调用。返回 true 表示事件已被绑定面板吃掉，不能再触发游戏/热键。
    /// </summary>
    public static bool TryCaptureKey(InputEventKey key)
    {
        if (_binding == BindTarget.None)
        {
            return false;
        }

        if (key == null || !key.Pressed || key.Echo)
        {
            return true;
        }

        Key code = key.Keycode;
        if (code == Key.Escape)
        {
            CancelBinding("已取消绑定");
            return true;
        }

        if (code == Key.None || code == Key.Shift || code == Key.Ctrl || code == Key.Alt || code == Key.Meta)
        {
            // 只按了修饰键：继续等真正的按键。
            return true;
        }

        var chord = new KeyChord(code, key.CtrlPressed, key.ShiftPressed, key.AltPressed);
        string conflict = FindConflict(_binding, chord);
        if (conflict != null)
        {
            SetStatus($"该按键已用于「{conflict}」，换一个", 4000, error: true);
            return true;
        }

        RewindSettings settings = RewindSettings.Instance;
        string label;
        switch (_binding)
        {
            case BindTarget.Toggle:
                settings.ToggleKey = chord;
                label = "打开 / 关闭";
                break;
            case BindTarget.Prev:
                settings.PrevKey = chord;
                label = "上一个节点";
                break;
            default:
                settings.NextKey = chord;
                label = "下一个节点";
                break;
        }

        _binding = BindTarget.None;
        settings.Save();
        HotkeysChanged?.Invoke();
        RefreshBindLabels();
        SetStatus($"已绑定「{label}」→ {chord.Display}", 4000);
        return true;
    }

    private static string FindConflict(BindTarget target, KeyChord chord)
    {
        RewindSettings settings = RewindSettings.Instance;
        if (target != BindTarget.Toggle && chord == settings.ToggleKey)
        {
            return "打开 / 关闭";
        }

        if (target != BindTarget.Prev && chord == settings.PrevKey)
        {
            return "上一个节点";
        }

        if (target != BindTarget.Next && chord == settings.NextKey)
        {
            return "下一个节点";
        }

        return null;
    }

    private static KeyChord ChordOf(BindTarget target)
    {
        RewindSettings settings = RewindSettings.Instance;
        switch (target)
        {
            case BindTarget.Toggle:
                return settings.ToggleKey;
            case BindTarget.Prev:
                return settings.PrevKey;
            case BindTarget.Next:
                return settings.NextKey;
            default:
                return default;
        }
    }

    private static void BeginBinding(BindTarget target)
    {
        _binding = target;
        RefreshBindLabels();
        SetStatus("按下要绑定的按键…（Esc 取消）", 60000);
    }

    private static void CancelBinding(string message)
    {
        if (_binding == BindTarget.None)
        {
            return;
        }

        _binding = BindTarget.None;
        RefreshBindLabels();
        SetStatus(message, 3000);
    }

    private static void RefreshBindLabels()
    {
        foreach (BindRow row in BindRows)
        {
            if (row.Button == null || !GodotObject.IsInstanceValid(row.Button))
            {
                continue;
            }

            bool active = _binding == row.Target;
            row.Button.Text = active ? "按下按键…" : ChordOf(row.Target).Display;
            row.Button.AddThemeColorOverride("font_color", active ? Accent : TextStrong);
        }

        if (_closeButton != null && GodotObject.IsInstanceValid(_closeButton))
        {
            _closeButton.TooltipText = $"关闭（{RewindSettings.Instance.ToggleKey.Display}）";
        }

        UpdateStatusHints(force: false);
    }

    // ---------------------------------------------------------------- 面板构建

    private static void EnsureLayer()
    {
        if (_layer != null && GodotObject.IsInstanceValid(_layer) && _layer.IsInsideTree())
        {
            return;
        }

        Node host = _hostProvider?.Invoke();
        if (host == null || !GodotObject.IsInstanceValid(host))
        {
            return;
        }

        var layer = new CanvasLayer { Layer = LayerIndex, Name = "GlobalRewindHud" };
        host.AddChild(layer);

        var root = new Control
        {
            Name = "Root",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(root);

        _layer = layer;
        _root = root;
        _frame = null;
        _bg = null;
        _graphFrame = null;
        _scroll = null;
        _canvas = null;
        _settingsPanel = null;
        _settingsButton = null;
        _closeButton = null;
        _grip = null;
        BindRows.Clear();

        ModLog.Info("hud layer created");
    }

    private static void EnsureBuilt()
    {
        EnsureLayer();
        if (_root == null)
        {
            return;
        }

        if (_frame != null && GodotObject.IsInstanceValid(_frame))
        {
            return;
        }

        BuildUi();
    }

    private static void BuildUi()
    {
        RewindSettings settings = RewindSettings.Instance;
        _zoom = Mathf.Clamp(settings.Zoom, 0.5f, 2f);

        var frame = new Control
        {
            Name = "RewindFrame",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };
        frame.Size = new Vector2(820, 600);
        _root.AddChild(frame);
        _frame = frame;

        _bg = new Panel { MouseFilter = Control.MouseFilterEnum.Stop };
        _bg.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        frame.AddChild(_bg);

        // 面板任意位置都接 Ctrl+滚轮（落在滚动区时会先被 ScrollContainer 吃掉）；
        // 标题/底栏上的滚轮冒泡到这里。
        frame.GuiInput += OnPanelGuiInput;

        var content = new MarginContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        content.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        content.AddThemeConstantOverride("margin_left", 16);
        content.AddThemeConstantOverride("margin_right", 16);
        content.AddThemeConstantOverride("margin_top", 14);
        content.AddThemeConstantOverride("margin_bottom", 12);
        frame.AddChild(content);

        var vbox = new VBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        vbox.AddThemeConstantOverride("separation", 10);
        content.AddChild(vbox);

        BuildHeader(vbox);
        BuildGraph(vbox);
        BuildFooter(vbox);
        BuildSettings(frame);
        BuildResizeGrip(frame);

        ApplyPanelStyle();
        UpdateAlphaLabel();
        UpdateZoomLabel();
        RefreshBindLabels();
        UpdateStatusHints(force: true);
    }

    private static void BuildHeader(Container parent)
    {
        var header = new HBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(0, 30),
            MouseDefaultCursorShape = Control.CursorShape.Move
        };
        header.AddThemeConstantOverride("separation", 10);
        parent.AddChild(header);

        _title = new Label
        {
            Text = "回溯树",
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Move
        };
        _title.AddThemeFontSizeOverride("font_size", 16);
        _title.AddThemeColorOverride("font_color", TextStrong);
        header.AddChild(_title);

        _subtitle = new Label
        {
            Text = string.Empty,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Stop,
            ClipText = true,
            MouseDefaultCursorShape = Control.CursorShape.Move
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 11);
        _subtitle.AddThemeColorOverride("font_color", TextMuted);
        header.AddChild(_subtitle);

        _settingsButton = MakeTextButton("按键", 12);
        _settingsButton.TooltipText = "打开按键绑定设置";
        _settingsButton.Pressed += ToggleSettingsPanel;
        header.AddChild(_settingsButton);

        _closeButton = MakeTextButton("×", 15);
        _closeButton.TooltipText = "关闭（Esc 不生效，按热键）";
        _closeButton.Pressed += Close;
        header.AddChild(_closeButton);

        header.GuiInput += OnHeaderGuiInput;
        _title.GuiInput += OnHeaderGuiInput;
        _subtitle.GuiInput += OnHeaderGuiInput;
    }

    private static void BuildGraph(Container parent)
    {
        _graphFrame = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        parent.AddChild(_graphFrame);

        _scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        _scroll.GuiInput += OnScrollGuiInput;
        _graphFrame.AddChild(_scroll);

        _canvas = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _scroll.AddChild(_canvas);
    }

    private static void BuildFooter(Container parent)
    {
        _status = new Label
        {
            Text = string.Empty,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _status.AddThemeFontSizeOverride("font_size", 10);
        _status.AddThemeColorOverride("font_color", TextMuted);
        parent.AddChild(_status);

        var footer = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Pass };
        footer.AddThemeConstantOverride("separation", 8);
        parent.AddChild(footer);

        var alphaTitle = new Label { Text = "背景", MouseFilter = Control.MouseFilterEnum.Ignore };
        alphaTitle.AddThemeFontSizeOverride("font_size", 10);
        alphaTitle.AddThemeColorOverride("font_color", TextMuted);
        footer.AddChild(alphaTitle);

        _alphaSlider = new HSlider
        {
            MinValue = 20,
            MaxValue = 100,
            Step = 1,
            Value = RewindSettings.Instance.BackgroundAlpha * 100f,
            CustomMinimumSize = new Vector2(96, 0),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Stop,
            TooltipText = "背景不透明度"
        };
        _alphaSlider.ValueChanged += OnAlphaChanged;
        _alphaSlider.DragEnded += _ => SaveSettings();
        footer.AddChild(_alphaSlider);

        _alphaLabel = new Label
        {
            Text = "93%",
            CustomMinimumSize = new Vector2(38, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _alphaLabel.AddThemeFontSizeOverride("font_size", 10);
        _alphaLabel.AddThemeColorOverride("font_color", TextMuted);
        footer.AddChild(_alphaLabel);

        footer.AddChild(new Control
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        });

        Button zoomOut = MakeTextButton("-", 14);
        zoomOut.TooltipText = "缩小";
        zoomOut.Pressed += () => ZoomStep(1f / 1.12f);
        footer.AddChild(zoomOut);

        _zoomLabel = new Label
        {
            Text = "100%",
            CustomMinimumSize = new Vector2(48, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        _zoomLabel.AddThemeFontSizeOverride("font_size", 10);
        _zoomLabel.AddThemeColorOverride("font_color", TextMuted);
        footer.AddChild(_zoomLabel);

        Button zoomIn = MakeTextButton("+", 14);
        zoomIn.TooltipText = "放大";
        zoomIn.Pressed += () => ZoomStep(1.12f);
        footer.AddChild(zoomIn);
    }

    private static void BuildSettings(Control frame)
    {
        _settingsPanel = new PanelContainer
        {
            Visible = false,
            CustomMinimumSize = new Vector2(300, 0),
            MouseFilter = Control.MouseFilterEnum.Stop
        };

        var style = new StyleBoxFlat
        {
            BgColor = new Color(0.070f, 0.078f, 0.098f, 0.98f),
            BorderColor = new Color(1f, 1f, 1f, 0.12f),
            ShadowColor = new Color(0f, 0f, 0f, 0.45f),
            ShadowSize = 14,
            ShadowOffset = new Vector2(0, 5)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(10);
        style.SetContentMarginAll(14);
        _settingsPanel.AddThemeStyleboxOverride("panel", style);
        frame.AddChild(_settingsPanel);

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        _settingsPanel.AddChild(box);

        var title = new Label { Text = "按键绑定" };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", TextStrong);
        box.AddChild(title);

        AddBindRow(box, BindTarget.Toggle, "打开 / 关闭");
        AddBindRow(box, BindTarget.Prev, "上一个节点");
        AddBindRow(box, BindTarget.Next, "下一个节点");

        var hint = new Label
        {
            Text = "点击右侧按钮后按下新按键，Esc 取消。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        hint.AddThemeFontSizeOverride("font_size", 10);
        hint.AddThemeColorOverride("font_color", TextMuted);
        box.AddChild(hint);

        Button reset = MakeTextButton("恢复默认键位", 11);
        reset.Pressed += ResetBindings;
        box.AddChild(reset);
    }

    /// <summary>
    /// 右下角握把：拖动改面板尺寸（尺寸落盘）。
    /// 只用 ColorRect 画三道斜线，避免依赖图标字体。
    /// </summary>
    private static void BuildResizeGrip(Control frame)
    {
        var grip = new Control
        {
            Name = "ResizeGrip",
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.Fdiagsize,
            TooltipText = "拖动调整面板大小"
        };
        frame.AddChild(grip);
        _grip = grip;
        grip.Size = new Vector2(GripSize, GripSize);
        PlaceGrip();

        for (int i = 0; i < 3; i++)
        {
            var line = new ColorRect
            {
                Color = new Color(1f, 1f, 1f, 0.20f + i * 0.08f),
                Position = new Vector2(6f + i * 4f, 24f),
                Size = new Vector2(9f + i * 2f, 1.5f),
                PivotOffset = Vector2.Zero,
                Rotation = -Mathf.Pi / 4f,
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            grip.AddChild(line);
        }

        grip.GuiInput += OnGripGuiInput;
    }

    /// <summary>握把贴在面板右下角（绝对定位，随尺寸变化重摆）。</summary>
    private static void PlaceGrip()
    {
        if (_grip == null || !GodotObject.IsInstanceValid(_grip) || _frame == null)
        {
            return;
        }

        _grip.Position = new Vector2(_frame.Size.X - GripSize - 4f, _frame.Size.Y - GripSize - 4f);
        _grip.Size = new Vector2(GripSize, GripSize);
    }

    private static void AddBindRow(Container parent, BindTarget target, string text)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        parent.AddChild(row);

        var label = new Label
        {
            Text = text,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", TextMuted);
        row.AddChild(label);

        var button = new Button
        {
            Text = string.Empty,
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(124, 24),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        button.AddThemeFontSizeOverride("font_size", 11);
        button.AddThemeColorOverride("font_color", TextStrong);
        button.AddThemeColorOverride("font_hover_color", new Color(1f, 1f, 1f));
        button.AddThemeColorOverride("font_pressed_color", new Color(1f, 1f, 1f));
        button.AddThemeStyleboxOverride("normal", KeycapBox(new Color(0.10f, 0.11f, 0.14f), 0.10f));
        button.AddThemeStyleboxOverride("hover", KeycapBox(new Color(0.15f, 0.17f, 0.21f), 0.20f));
        button.AddThemeStyleboxOverride("pressed", KeycapBox(new Color(0.18f, 0.22f, 0.28f), 0.30f));
        button.AddThemeStyleboxOverride("focus", KeycapBox(new Color(0f, 0f, 0f, 0f), 0f));

        BindTarget captured = target;
        button.Pressed += () => BeginBinding(captured);
        row.AddChild(button);

        BindRows.Add(new BindRow { Target = target, Button = button });
    }

    private static Button MakeTextButton(string text, int fontSize)
    {
        var button = new Button
        {
            Text = text,
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Stop,
            CustomMinimumSize = new Vector2(0, 24)
        };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", TextMuted);
        button.AddThemeColorOverride("font_hover_color", TextStrong);
        button.AddThemeColorOverride("font_pressed_color", TextStrong);
        return button;
    }

    private static StyleBoxFlat KeycapBox(Color color, float borderAlpha)
    {
        var style = new StyleBoxFlat
        {
            BgColor = color,
            BorderColor = new Color(1f, 1f, 1f, borderAlpha)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(6);
        style.SetContentMarginAll(4);
        return style;
    }

    // ---------------------------------------------------------------- 刷新与布局

    private static void Refresh(bool centerFocused)
    {
        try
        {
            if (_source == null)
            {
                return;
            }

            TreeOverlaySnapshot snapshot = _source.Capture() ?? TreeOverlaySnapshot.Empty;
            _snapshot = snapshot;

            if (_collapsedSession != snapshot.SessionKey)
            {
                Collapsed.Clear();
                _collapsedSession = snapshot.SessionKey;
                _lastFocusedId = 0;
            }

            DropStaleCollapsed(snapshot);

            if (_focusLinkFrom != 0 && !HasLink(snapshot, _focusLinkFrom, _focusLinkTo))
            {
                _focusLinkFrom = 0;
                _focusLinkTo = 0;
            }

            if (snapshot.FocusedId != _lastFocusedId)
            {
                ExpandAncestors(snapshot, snapshot.FocusedId);
                _lastFocusedId = snapshot.FocusedId;
            }

            UpdateHeader(snapshot);
            RebuildGraph(snapshot, centerFocused, null);
            UpdateStatusHints(force: false);
        }
        catch (Exception e)
        {
            ModLog.Warn($"refresh hud failed: {e.Message}");
        }
    }

    private static void DropStaleCollapsed(TreeOverlaySnapshot snapshot)
    {
        if (Collapsed.Count == 0)
        {
            return;
        }

        var alive = new HashSet<int>();
        foreach (TreeNodeView node in snapshot.Nodes)
        {
            if (node != null)
            {
                alive.Add(node.Id);
            }
        }

        List<int> stale = null;
        foreach (int id in Collapsed)
        {
            if (!alive.Contains(id))
            {
                stale ??= new List<int>();
                stale.Add(id);
            }
        }

        if (stale == null)
        {
            return;
        }

        foreach (int id in stale)
        {
            Collapsed.Remove(id);
        }
    }

    /// <summary>
    /// 焦点节点换了（新抓点 / 跳转 / F6）时，展开它上面所有被折叠的祖先，
    /// 保证"焦点一定看得见"。用户手动折叠焦点节点的祖先时，FocusedId 没变，
    /// 这里不会强行展开 —— 折叠是用户的选择。
    /// </summary>
    private static void ExpandAncestors(TreeOverlaySnapshot snapshot, int focusedId)
    {
        if (focusedId == 0)
        {
            return;
        }

        var byId = new Dictionary<int, TreeNodeView>(snapshot.Nodes.Count);
        foreach (TreeNodeView node in snapshot.Nodes)
        {
            if (node != null)
            {
                byId[node.Id] = node;
            }
        }

        int id = focusedId;
        int guard = 0;
        while (id != 0 && guard++ < 10000 && byId.TryGetValue(id, out TreeNodeView node))
        {
            int parentId = node.ParentId;
            if (parentId != 0)
            {
                Collapsed.Remove(parentId);
            }

            id = parentId;
        }
    }

    private static void UpdateHeader(TreeOverlaySnapshot snapshot)
    {
        if (_title != null)
        {
            _title.Text = snapshot.Title;
        }

        if (_subtitle != null)
        {
            _subtitle.Text = snapshot.Subtitle;
        }
    }

    private static void RebuildGraph(TreeOverlaySnapshot snapshot, bool centerFocused, Vector2? scrollTarget)
    {
        if (_canvas == null || !GodotObject.IsInstanceValid(_canvas) || _scroll == null)
        {
            return;
        }

        int keepX = _scroll.ScrollHorizontal;
        int keepY = _scroll.ScrollVertical;

        TreeLayout layout = TreeLayout.Build(snapshot, Collapsed);
        _layout = layout;

        int focusedId = snapshot.FocusedId;
        RewindGraphView.Populate(_canvas, layout, _zoom, focusedId, snapshot.EmptyText,
            ActivateNode, ContextNode, OnCollapseToggled, OnLinkToggled,
            _focusLinkFrom, _focusLinkTo);

        Vector2 target;
        if (scrollTarget.HasValue)
        {
            target = scrollTarget.Value;
        }
        else if (centerFocused && !layout.IsEmpty && layout.ById.TryGetValue(focusedId, out TreeLayout.Entry entry))
        {
            float viewWidth = _scroll.Size.X > 4f ? _scroll.Size.X : _frame.Size.X - 32f;
            float viewHeight = _scroll.Size.Y > 4f ? _scroll.Size.Y : _frame.Size.Y - 150f;
            target = new Vector2(
                entry.X * _zoom - viewWidth / 2f,
                entry.Y * _zoom - viewHeight / 2f);
        }
        else
        {
            target = new Vector2(keepX, keepY);
        }

        RequestScroll(target);
    }

    // ---------------------------------------------------------------- 交互回调

    /// <summary>左键激活：UI 不知道语义，只把事件交给上层。</summary>
    private static void ActivateNode(int id)
    {
        NodeActivated?.Invoke(id);
    }

    /// <summary>右键激活：同上。</summary>
    private static void ContextNode(int id)
    {
        NodeContextActivated?.Invoke(id);
    }

    /// <summary>点击虚线：聚焦/取消聚焦这条来路（节点本身仍然管跳转）。</summary>
    private static void OnLinkToggled(int from, int to)
    {
        if (_focusLinkFrom == from && _focusLinkTo == to)
        {
            _focusLinkFrom = 0;
            _focusLinkTo = 0;
            SetStatus("已取消聚焦", 2000);
        }
        else
        {
            _focusLinkFrom = from;
            _focusLinkTo = to;
            SetStatus($"聚焦 #{from} → #{to} 的来路", 3000);
        }

        Refresh(centerFocused: false);
    }

    private static bool HasLink(TreeOverlaySnapshot snapshot, int from, int to)
    {
        foreach (TreeLinkView link in snapshot.Links)
        {
            if (link != null && link.From == from && link.To == to)
            {
                return true;
            }
        }

        return false;
    }

    private static void OnCollapseToggled(int id)
    {
        bool expanded = Collapsed.Remove(id);
        if (!expanded)
        {
            TreeNodeView node = FindNode(_snapshot, id);
            if (node == null || node.Children.Length == 0)
            {
                return;
            }

            Collapsed.Add(id);
        }

        Refresh(centerFocused: false);

        int hidden = 0;
        if (_layout != null && _layout.ById.TryGetValue(id, out TreeLayout.Entry entry))
        {
            hidden = entry.HiddenCount;
        }

        SetStatus(expanded
            ? $"已展开 #{id} 的子树"
            : $"已折叠 #{id} 的子树（{hidden} 个节点）", 3000);
    }

    private static TreeNodeView FindNode(TreeOverlaySnapshot snapshot, int id)
    {
        foreach (TreeNodeView node in snapshot.Nodes)
        {
            if (node != null && node.Id == id)
            {
                return node;
            }
        }

        return null;
    }

    private static void OnScrollGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouse
            || !mouse.Pressed
            || !mouse.CtrlPressed
            || (mouse.ButtonIndex != MouseButton.WheelUp && mouse.ButtonIndex != MouseButton.WheelDown))
        {
            return;
        }

        _scroll.AcceptEvent();

        float factor = mouse.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1f / 1.12f;
        ZoomBy(factor, _scroll.GetLocalMousePosition());
    }

    /// <summary>面板空白处：左键起手拖动、Ctrl+滚轮缩放（滚动区自己会先处理滚轮）。</summary>
    private static void OnPanelGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouse || !mouse.Pressed)
        {
            return;
        }

        if (mouse.ButtonIndex == MouseButton.Left)
        {
            Vector2 position = _root.GetGlobalMousePosition();

            // 图区、设置面板、右下握把各有自己的处理，不要在这里抢。
            if (IsUnder(_grip, position) || IsUnder(_scroll, position)
                || (_settingsPanel != null && _settingsPanel.Visible && IsUnder(_settingsPanel, position)))
            {
                return;
            }

            BeginDrag();
            _frame.AcceptEvent();
            return;
        }

        if (!mouse.CtrlPressed
            || (mouse.ButtonIndex != MouseButton.WheelUp && mouse.ButtonIndex != MouseButton.WheelDown))
        {
            return;
        }

        _frame.AcceptEvent();

        Vector2 mousePosition = _root.GetGlobalMousePosition();
        Vector2 focus = _scroll.GetGlobalRect().HasPoint(mousePosition)
            ? _scroll.GetLocalMousePosition()
            : _scroll.Size / 2f;

        float factor = mouse.ButtonIndex == MouseButton.WheelUp ? 1.12f : 1f / 1.12f;
        ZoomBy(factor, focus);
    }

    private static bool IsUnder(Control control, Vector2 globalPosition)
    {
        return control != null
               && GodotObject.IsInstanceValid(control)
               && control.IsVisibleInTree()
               && control.GetGlobalRect().HasPoint(globalPosition);
    }

    private static void ZoomStep(float factor)
    {
        ZoomBy(factor, _scroll.Size / 2f);
    }

    private static void ZoomBy(float factor, Vector2 focus)
    {
        if (_source == null)
        {
            return;
        }

        float previous = _zoom;
        float next = Mathf.Clamp(previous * factor, 0.5f, 2f);
        if (Mathf.Abs(next - previous) < 0.001f)
        {
            return;
        }

        Vector2 viewport = new Vector2(_scroll.ScrollHorizontal, _scroll.ScrollVertical);
        Vector2 contentPoint = (viewport + focus) / previous;

        _zoom = next;
        RewindSettings.Instance.Zoom = next;
        UpdateZoomLabel();

        Vector2 target = contentPoint * next - focus;
        RebuildGraph(_snapshot, centerFocused: false, target);
        SaveSettings();
    }

    private static void OnAlphaChanged(double value)
    {
        RewindSettings.Instance.BackgroundAlpha = (float)(value / 100.0);
        ApplyPanelStyle();
        UpdateAlphaLabel();
    }

    private static void UpdateAlphaLabel()
    {
        if (_alphaLabel != null)
        {
            _alphaLabel.Text = $"{Mathf.RoundToInt(RewindSettings.Instance.BackgroundAlpha * 100f)}%";
        }
    }

    private static void UpdateZoomLabel()
    {
        if (_zoomLabel != null)
        {
            _zoomLabel.Text = $"{Mathf.RoundToInt(_zoom * 100f)}%";
        }
    }

    private static void ApplyPanelStyle()
    {
        if (_bg == null || _graphFrame == null)
        {
            return;
        }

        float alpha = Mathf.Clamp(RewindSettings.Instance.BackgroundAlpha, 0.2f, 1f);

        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.052f, 0.058f, 0.072f, alpha),
            BorderColor = new Color(1f, 1f, 1f, 0.08f + 0.05f * alpha),
            ShadowColor = new Color(0f, 0f, 0f, 0.40f * alpha),
            ShadowSize = 18,
            ShadowOffset = new Vector2(0, 7)
        };
        panelStyle.SetBorderWidthAll(1);
        panelStyle.SetCornerRadiusAll(14);
        _bg.AddThemeStyleboxOverride("panel", panelStyle);

        var inset = new StyleBoxFlat
        {
            BgColor = new Color(0.016f, 0.020f, 0.027f, Mathf.Clamp(0.18f + alpha * 0.50f, 0.20f, 0.72f)),
            BorderColor = new Color(1f, 1f, 1f, 0.06f)
        };
        inset.SetBorderWidthAll(1);
        inset.SetCornerRadiusAll(10);
        inset.SetContentMarginAll(8);
        _graphFrame.AddThemeStyleboxOverride("panel", inset);
    }

    private static void ToggleSettingsPanel()
    {
        if (_settingsPanel == null)
        {
            return;
        }

        _settingsPanel.Visible = !_settingsPanel.Visible;
        if (_settingsPanel.Visible)
        {
            PositionSettingsPanel();
        }
        else
        {
            CancelBinding("已关闭按键设置");
        }
    }

    private static void PositionSettingsPanel()
    {
        if (_settingsPanel == null || !GodotObject.IsInstanceValid(_settingsPanel) || _frame == null)
        {
            return;
        }

        Vector2 size = _settingsPanel.GetCombinedMinimumSize();
        if (_settingsPanel.Size != size)
        {
            _settingsPanel.Size = size;
        }

        _settingsPanel.Position = new Vector2(
            Mathf.Max(12f, _frame.Size.X - size.X - 14f),
            46f);
    }

    private static void ResetBindings()
    {
        RewindSettings settings = RewindSettings.Instance;
        settings.ToggleKey = new KeyChord(Key.F7);
        settings.PrevKey = new KeyChord(Key.F6);
        settings.NextKey = new KeyChord(Key.F6, shift: true);
        settings.Save();
        HotkeysChanged?.Invoke();
        _binding = BindTarget.None;
        RefreshBindLabels();
        SetStatus("已恢复默认键位（F7 / F6 / Shift+F6）", 4000);
    }

    // ---------------------------------------------------------------- 面板拖动 / 缩放

    private static void OnHeaderGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouse
            || mouse.ButtonIndex != MouseButton.Left
            || !mouse.Pressed)
        {
            return;
        }

        BeginDrag();
        _frame.AcceptEvent();
    }

    private static void BeginDrag()
    {
        _dragging = true;
        _resizing = false;
        _dragOffset = _frame.GetGlobalMousePosition() - _frame.GlobalPosition;
        _frame.MoveToFront();
    }

    private static void OnGripGuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mouse
            || mouse.ButtonIndex != MouseButton.Left
            || !mouse.Pressed)
        {
            return;
        }

        _resizing = true;
        _dragging = false;
        _resizeStartMouse = _root.GetGlobalMousePosition();
        _resizeStartSize = _frame.Size;
        _frame.MoveToFront();
        _grip.AcceptEvent();
    }

    private static void UpdateDrag()
    {
        if (!Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _dragging = false;
            ClampPanelToViewport();
            SaveSettings();
            return;
        }

        _frame.GlobalPosition = _root.GetGlobalMousePosition() - _dragOffset;
    }

    private static void UpdateResize()
    {
        if (!Input.IsMouseButtonPressed(MouseButton.Left))
        {
            _resizing = false;

            RewindSettings settings = RewindSettings.Instance;
            settings.PanelWidth = _frame.Size.X;
            settings.PanelHeight = _frame.Size.Y;
            SaveSettings();
            PositionSettingsPanel();
            ModLog.Info($"hud resized size={_frame.Size}");
            return;
        }

        Vector2 viewport = _root.GetViewportRect().Size;
        Vector2 delta = _root.GetGlobalMousePosition() - _resizeStartMouse;
        float maxWidth = Mathf.Max(MinPanelWidth, viewport.X - 24f);
        float maxHeight = Mathf.Max(MinPanelHeight, viewport.Y - 48f);

        _frame.Size = new Vector2(
            Mathf.Clamp(_resizeStartSize.X + delta.X, MinPanelWidth, maxWidth),
            Mathf.Clamp(_resizeStartSize.Y + delta.Y, MinPanelHeight, maxHeight));
        PlaceGrip();
    }

    private static void PlacePanel()
    {
        RewindSettings settings = RewindSettings.Instance;
        Vector2 viewport = _root.GetViewportRect().Size;

        float width = Mathf.Clamp(settings.PanelWidth, MinPanelWidth,
            Mathf.Max(MinPanelWidth, viewport.X - 40f));
        float height = Mathf.Clamp(settings.PanelHeight, MinPanelHeight,
            Mathf.Max(MinPanelHeight, viewport.Y - 60f));
        _frame.Size = new Vector2(width, height);
        PlaceGrip();

        Vector2 position = settings.PanelX >= 0f && settings.PanelY >= 0f
            ? new Vector2(settings.PanelX, settings.PanelY)
            : new Vector2((viewport.X - width) / 2f, (viewport.Y - height) / 2f - 10f);

        _frame.Position = position;
        ClampPanelToViewport();

        ModLog.Info($"hud panel pos={_frame.Position} size={_frame.Size} viewport={viewport} " +
                    $"min={_frame.GetCombinedMinimumSize()}");
    }

    private static void ClampPanelToViewport()
    {
        if (_frame == null || !GodotObject.IsInstanceValid(_frame) || _root == null)
        {
            return;
        }

        Vector2 viewport = _root.GetViewportRect().Size;
        Vector2 size = _frame.Size;

        // 屏幕变小（换分辨率/全屏切换）时把面板一起收回视野内。
        float clampedWidth = Mathf.Min(size.X, Mathf.Max(MinPanelWidth, viewport.X));
        float clampedHeight = Mathf.Min(size.Y, Mathf.Max(MinPanelHeight, viewport.Y));
        if (Mathf.Abs(clampedWidth - size.X) > 0.5f || Mathf.Abs(clampedHeight - size.Y) > 0.5f)
        {
            size = new Vector2(clampedWidth, clampedHeight);
            _frame.Size = size;
        }

        float minVisible = 170f;

        float x = Mathf.Clamp(_frame.Position.X, -size.X + minVisible, Mathf.Max(0f, viewport.X - minVisible));
        float y = Mathf.Clamp(_frame.Position.Y, 0f, Mathf.Max(0f, viewport.Y - 52f));
        _frame.Position = new Vector2(x, y);

        RewindSettings settings = RewindSettings.Instance;
        settings.PanelX = x;
        settings.PanelY = y;
    }

    private static void SaveSettings()
    {
        RewindSettings.Instance.Save();
    }

    // ---------------------------------------------------------------- 滚动与状态

    private static void RequestScroll(Vector2 target)
    {
        _pendingScroll = true;
        _pendingScrollX = Mathf.Max(0, Mathf.RoundToInt(target.X));
        _pendingScrollY = Mathf.Max(0, Mathf.RoundToInt(target.Y));
        ApplyPendingScroll();
    }

    private static void ApplyPendingScroll()
    {
        if (_scroll == null || !GodotObject.IsInstanceValid(_scroll))
        {
            return;
        }

        _scroll.ScrollHorizontal = _pendingScrollX;
        _scroll.ScrollVertical = _pendingScrollY;
    }

    private static void OnProcessFrame()
    {
        try
        {
            if (!_open)
            {
                return;
            }

            if (_frame == null || !GodotObject.IsInstanceValid(_frame))
            {
                _open = false;
                return;
            }

            if (_source == null || !_source.CanShow)
            {
                Close();
                return;
            }

            if (_dragging)
            {
                UpdateDrag();
            }

            if (_resizing)
            {
                UpdateResize();
            }

            UpdateStatusHints(force: false);

            if (_settingsPanel != null && GodotObject.IsInstanceValid(_settingsPanel) && _settingsPanel.Visible)
            {
                PositionSettingsPanel();
            }

            if (_pendingScroll)
            {
                // 等一帧：ScrollContainer 在布局排序后才会更新滚动范围，立即设会被旧上限截断。
                _pendingScroll = false;
                ApplyPendingScroll();
            }
        }
        catch (Exception e)
        {
            ModLog.Warn($"hud frame failed: {e.Message}");
        }
    }

    private static string HintText
    {
        get
        {
            string baseHint = string.IsNullOrEmpty(_snapshot.Hint) ? FallbackHint : _snapshot.Hint;
            return $"{baseHint} · {RewindSettings.Instance.ToggleKey.Display} 开关";
        }
    }

    private static void UpdateStatusHints(bool force)
    {
        if (_status == null || !GodotObject.IsInstanceValid(_status))
        {
            return;
        }

        ulong now = Time.GetTicksMsec();
        if (!force && (_statusUntil == 0 || now < _statusUntil))
        {
            return;
        }

        _status.Text = HintText;
        _status.AddThemeColorOverride("font_color", TextMuted);
        _statusUntil = 0;
    }

    private static void SetStatus(string message, ulong durationMs = 4000, bool error = false)
    {
        if (_status == null || !GodotObject.IsInstanceValid(_status))
        {
            return;
        }

        _status.Text = string.IsNullOrEmpty(message) ? "操作失败" : message;
        _status.AddThemeColorOverride("font_color", error ? TextWarn : TextStrong);
        _statusUntil = Time.GetTicksMsec() + durationMs;
    }
}
