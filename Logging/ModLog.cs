using Godot;

namespace GlobalRewind.Logging;

/// <summary>
/// 统一日志前缀，方便在 godot.log 里过滤：[GlobalRewind]
/// </summary>
internal static class ModLog
{
    private const string Prefix = "[GlobalRewind] ";

    public static void Info(string message)
    {
        GD.Print(Prefix + message);
    }

    public static void Warn(string message)
    {
        GD.PushWarning(Prefix + message);
    }

    public static void Error(string message)
    {
        GD.PushError(Prefix + message);
    }
}
