using System;
using System.Globalization;
using System.IO;
using System.Text;
using Godot;
using GlobalRewind.Logging;

namespace GlobalRewind.Presentation;

/// <summary>
/// 一个可绑定的按键（可带修饰键）。
/// 只用 `Keycode`，不区分物理键位 —— 与游戏自己的热键行为一致。
/// </summary>
internal readonly struct KeyChord : IEquatable<KeyChord>
{
    public readonly Key Code;
    public readonly bool Ctrl;
    public readonly bool Shift;
    public readonly bool Alt;

    public KeyChord(Key code, bool ctrl = false, bool shift = false, bool alt = false)
    {
        Code = code;
        Ctrl = ctrl;
        Shift = shift;
        Alt = alt;
    }

    public bool IsValid => Code != Key.None;

    public bool IsModifierOnly =>
        Code == Key.Shift || Code == Key.Ctrl || Code == Key.Alt || Code == Key.Meta;

    public bool Matches(InputEventKey key)
    {
        return key != null
               && IsValid
               && key.Keycode == Code
               && key.CtrlPressed == Ctrl
               && key.ShiftPressed == Shift
               && key.AltPressed == Alt;
    }

    /// <summary>轮询版本：三条输入通路里最可靠的一条（不依赖任何回调）。</summary>
    public bool IsDown()
    {
        if (!IsValid || !Input.IsKeyPressed(Code))
        {
            return false;
        }

        return Input.IsKeyPressed(Key.Ctrl) == Ctrl
               && Input.IsKeyPressed(Key.Shift) == Shift
               && Input.IsKeyPressed(Key.Alt) == Alt;
    }

    public string Display
    {
        get
        {
            if (!IsValid)
            {
                return "未绑定";
            }

            var builder = new StringBuilder();
            if (Ctrl)
            {
                builder.Append("Ctrl+");
            }

            if (Alt)
            {
                builder.Append("Alt+");
            }

            if (Shift)
            {
                builder.Append("Shift+");
            }

            string name = OS.GetKeycodeString(Code);
            builder.Append(string.IsNullOrEmpty(name) ? Code.ToString() : name);
            return builder.ToString();
        }
    }

    public static KeyChord Parse(string text, KeyChord fallback)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        bool ctrl = false;
        bool shift = false;
        bool alt = false;
        Key code = Key.None;

        string[] parts = text.Trim().Split('+');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.Length == 0)
            {
                continue;
            }

            if (i < parts.Length - 1)
            {
                switch (part.ToLowerInvariant())
                {
                    case "ctrl":
                    case "control":
                        ctrl = true;
                        continue;
                    case "shift":
                        shift = true;
                        continue;
                    case "alt":
                        alt = true;
                        continue;
                }
            }

            Key parsed = OS.FindKeycodeFromString(part);
            if (parsed == Key.None)
            {
                Enum.TryParse(part, true, out parsed);
            }

            if (parsed != Key.None)
            {
                code = parsed;
            }
        }

        return code == Key.None ? fallback : new KeyChord(code, ctrl, shift, alt);
    }

    public bool Equals(KeyChord other)
    {
        return Code == other.Code && Ctrl == other.Ctrl && Shift == other.Shift && Alt == other.Alt;
    }

    public override bool Equals(object obj)
    {
        return obj is KeyChord other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = (int)Code;
            hash = (hash * 397) ^ (Ctrl ? 1 : 0);
            hash = (hash * 397) ^ (Shift ? 1 : 0);
            hash = (hash * 397) ^ (Alt ? 1 : 0);
            return hash;
        }
    }

    public static bool operator ==(KeyChord left, KeyChord right) => left.Equals(right);

    public static bool operator !=(KeyChord left, KeyChord right) => !left.Equals(right);
}

/// <summary>
/// HUD 的本地设置。写在 %APPDATA%\SlayTheSpire2\GlobalRewind\ui.cfg，
/// **不能**放在 global_rewind 运行目录里 —— 那里每次开局都会被 RewindStore.DeleteAll 清掉。
/// </summary>
internal sealed class RewindSettings
{
    public static RewindSettings Instance { get; } = new RewindSettings();

    public float BackgroundAlpha { get; set; } = 0.93f;
    public float Zoom { get; set; } = 1f;
    public float PanelX { get; set; } = -1f;
    public float PanelY { get; set; } = -1f;
    public float PanelWidth { get; set; } = 820f;
    public float PanelHeight { get; set; } = 600f;

    public KeyChord ToggleKey { get; set; } = new KeyChord(Key.F7);
    public KeyChord PrevKey { get; set; } = new KeyChord(Key.F6);
    public KeyChord NextKey { get; set; } = new KeyChord(Key.F6, shift: true);

    public static string ConfigPath
    {
        get
        {
            string appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData))
            {
                appData = OS.GetUserDataDir();
            }

            return Path.Combine(appData, "SlayTheSpire2", "GlobalRewind", "ui.cfg");
        }
    }

    public void Load()
    {
        try
        {
            string path = ConfigPath;
            if (!File.Exists(path))
            {
                return;
            }

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                {
                    continue;
                }

                int split = line.IndexOf('=');
                if (split <= 0)
                {
                    continue;
                }

                string key = line.Substring(0, split).Trim().ToLowerInvariant();
                string value = line.Substring(split + 1).Trim();

                switch (key)
                {
                    case "alpha":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float alpha))
                        {
                            BackgroundAlpha = Mathf.Clamp(alpha, 0.2f, 1f);
                        }

                        break;
                    case "zoom":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float zoom))
                        {
                            Zoom = Mathf.Clamp(zoom, 0.5f, 2f);
                        }

                        break;
                    case "panel_x":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float px))
                        {
                            PanelX = px;
                        }

                        break;
                    case "panel_y":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float py))
                        {
                            PanelY = py;
                        }

                        break;
                    case "panel_w":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float pw))
                        {
                            PanelWidth = Mathf.Clamp(pw, 420f, 4096f);
                        }

                        break;
                    case "panel_h":
                        if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float ph))
                        {
                            PanelHeight = Mathf.Clamp(ph, 280f, 4096f);
                        }

                        break;
                    case "key_toggle":
                        ToggleKey = KeyChord.Parse(value, ToggleKey);
                        break;
                    case "key_prev":
                        PrevKey = KeyChord.Parse(value, PrevKey);
                        break;
                    case "key_next":
                        NextKey = KeyChord.Parse(value, NextKey);
                        break;
                }
            }

            ModLog.Info($"settings loaded: alpha={BackgroundAlpha:0.00} zoom={Zoom:0.00} " +
                        $"keys={ToggleKey.Display}/{PrevKey.Display}/{NextKey.Display}");
        }
        catch (Exception e)
        {
            ModLog.Warn($"load settings failed: {e.Message}");
        }
    }

    public void Save()
    {
        try
        {
            string path = ConfigPath;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var builder = new StringBuilder();
            builder.AppendLine("# Global Rewind HUD settings");
            builder.AppendLine("alpha=" + BackgroundAlpha.ToString("0.###", CultureInfo.InvariantCulture));
            builder.AppendLine("zoom=" + Zoom.ToString("0.###", CultureInfo.InvariantCulture));
            builder.AppendLine("panel_x=" + PanelX.ToString("0.#", CultureInfo.InvariantCulture));
            builder.AppendLine("panel_y=" + PanelY.ToString("0.#", CultureInfo.InvariantCulture));
            builder.AppendLine("panel_w=" + PanelWidth.ToString("0.#", CultureInfo.InvariantCulture));
            builder.AppendLine("panel_h=" + PanelHeight.ToString("0.#", CultureInfo.InvariantCulture));
            builder.AppendLine("key_toggle=" + ToggleKey.Display);
            builder.AppendLine("key_prev=" + PrevKey.Display);
            builder.AppendLine("key_next=" + NextKey.Display);

            File.WriteAllText(path, builder.ToString());
        }
        catch (Exception e)
        {
            ModLog.Warn($"save settings failed: {e.Message}");
        }
    }
}
