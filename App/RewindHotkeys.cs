using Godot;
using GlobalRewind.Logging;
using GlobalRewind.Presentation;

namespace GlobalRewind.App;

internal enum RewindAction
{
    ToggleHud,
    JumpParent,
    JumpForward
}

/// <summary>
/// 热键分发（应用层，不属于 UI 也不是领域逻辑）。
///
/// 键位来自 <see cref="RewindSettings"/>：
///   · 事件通路（Harmony 的 NGame._Input / 常驻 Node）按键位匹配后分发；
///   · ProcessFrame 轮询按当前键位查 Input.IsKeyPressed，作为兜底；
///   · 两条通路都走 <see cref="Dispatch"/>，用 150ms 窗口去重同一次按下。
///
/// UI 只负责"开关/显示状态/吃绑定键"，跳转与前进由本类直接调逻辑层。
/// </summary>
internal static class RewindHotkeys
{
    private const ulong DedupeWindowMs = 150;

    private static RewindAction _lastAction;
    private static ulong _lastFiredMs;

    private static bool _toggleDown;
    private static bool _prevDown;
    private static bool _nextDown;

    /// <summary>
    /// 刚完成一次按键绑定：这个物理按键在松开之前不能再触发任何热键。
    /// 否则"按下 F9 绑定 F9"会在同一次按键里立刻把刚打开的 HUD 关掉
    /// （事件通路 + 轮询通路都可能看到同一次按下）。
    /// </summary>
    private static bool _suppressUntilRelease;

    // ---------------------------------------------------------------- 事件入口

    /// <summary>
    /// 所有输入通路（Harmony / 常驻 Node / UnhandledInput）共用。
    /// 绑定面板优先吃按键；其余按当前键位匹配。
    /// </summary>
    public static void HandleEvent(InputEvent inputEvent, string source)
    {
        if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        if (RewindHud.TryCaptureKey(key))
        {
            return;
        }

        var chord = new KeyChord(key.Keycode, key.CtrlPressed, key.ShiftPressed, key.AltPressed);
        if (TryMatch(chord, out RewindAction action))
        {
            Dispatch(action, source);
        }
    }

    public static bool TryMatch(KeyChord chord, out RewindAction action)
    {
        RewindSettings settings = RewindSettings.Instance;

        if (_suppressUntilRelease && chord.IsDown())
        {
            action = default;
            return false;
        }

        if (chord.IsValid && chord == settings.ToggleKey)
        {
            action = RewindAction.ToggleHud;
            return true;
        }

        if (chord.IsValid && chord == settings.PrevKey)
        {
            action = RewindAction.JumpParent;
            return true;
        }

        if (chord.IsValid && chord == settings.NextKey)
        {
            action = RewindAction.JumpForward;
            return true;
        }

        action = default;
        return false;
    }

    public static void Dispatch(RewindAction action, string source)
    {
        try
        {
            ulong now = Time.GetTicksMsec();
            if (action == _lastAction && now - _lastFiredMs < DedupeWindowMs)
            {
                return;
            }

            _lastAction = action;
            _lastFiredMs = now;

            switch (action)
            {
                case RewindAction.ToggleHud:
                    ModLog.Info($"input: toggle hud via {source}");
                    RewindHud.Toggle();
                    break;

                case RewindAction.JumpParent:
                case RewindAction.JumpForward:
                {
                    RewindRuntime runtime = RewindRuntime.Instance;
                    bool ok = action == RewindAction.JumpParent
                        ? runtime.JumpToParent(out string error)
                        : runtime.JumpToForward(out error);

                    if (!ok)
                    {
                        RewindHud.ShowStatus(error, error: true);
                    }

                    break;
                }
            }
        }
        catch (System.Exception e)
        {
            ModLog.Warn($"hotkey ({source}) failed: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- 轮询兜底

    /// <summary>
    /// 重新同步轮询状态。按当前键位的实际按下状态播种，而不是一律置 false ——
    /// 否则刚把某个键绑定上去时，轮询会把这个"还按着"的键当成一次新的按下，
    /// 立刻再触发一次动作（比如绑定 F9 后立刻把刚打开的 HUD 关掉）。
    /// </summary>
    public static void ResetPollState()
    {
        RewindSettings settings = RewindSettings.Instance;
        _toggleDown = settings.ToggleKey.IsDown();
        _prevDown = settings.PrevKey.IsDown();
        _nextDown = settings.NextKey.IsDown();
    }

    public static void SuppressUntilRelease()
    {
        _suppressUntilRelease = true;
        ResetPollState();
    }

    private static void ClearSuppressionWhenReleased()
    {
        if (!_suppressUntilRelease)
        {
            return;
        }

        RewindSettings settings = RewindSettings.Instance;
        bool anyDown = settings.ToggleKey.IsDown()
                       || settings.PrevKey.IsDown()
                       || settings.NextKey.IsDown();
        if (anyDown)
        {
            return;
        }

        _suppressUntilRelease = false;
        ResetPollState();
    }

    /// <summary>每帧轮询当前绑定的三个键。绑定面板正在等按键时跳过，避免抢输入。</summary>
    public static void Poll()
    {
        if (RewindHud.IsCapturingKey)
        {
            ResetPollState();
            return;
        }

        ClearSuppressionWhenReleased();
        if (_suppressUntilRelease)
        {
            return;
        }

        RewindSettings settings = RewindSettings.Instance;
        PollOne(settings.ToggleKey, RewindAction.ToggleHud, ref _toggleDown);
        PollOne(settings.PrevKey, RewindAction.JumpParent, ref _prevDown);
        PollOne(settings.NextKey, RewindAction.JumpForward, ref _nextDown);
    }

    private static void PollOne(KeyChord chord, RewindAction action, ref bool wasDown)
    {
        bool down = chord.IsDown();
        if (down && !wasDown)
        {
            Dispatch(action, "poll");
        }

        wasDown = down;
    }
}
