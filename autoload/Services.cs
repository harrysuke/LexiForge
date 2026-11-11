using System;
using Godot;
using LexiForge.Core;

namespace LexiForge.Autoload;

/// <summary>
/// Shared, Godot-facing conveniences only. The autoload singletons themselves
/// now live one-class-per-file (EventBus.cs, ConfigService.cs, ...) because a
/// C# autoload registered by script path must resolve to a single public Node
/// subclass whose name matches the file. Packing nine autoloads into one file
/// was an architectural sketch, not something Godot can register.
/// </summary>
public static class GameServices
{
    public static BalanceConfig Config => ConfigService.Instance.Config;
    public static SaveData Save => SaveService.Instance.Data;
    public static long NowUnix => (long)Time.GetUnixTimeFromSystem();
    public static int UtcDay => (int)(NowUnix / 86_400L);

    /// <summary>Safe-area padding for notch/cutout devices (GDD 11.1).</summary>
    public static Vector2I SafeAreaOrigin()
    {
        Rect2I safe = DisplayServer.GetDisplaySafeArea();
        Rect2I window = new(0, 0,
                            DisplayServer.WindowGetSize().X,
                            DisplayServer.WindowGetSize().Y);
        return new Vector2I(
            Math.Max(0, safe.Position.X - window.Position.X),
            Math.Max(0, safe.Position.Y - window.Position.Y));
    }
}
