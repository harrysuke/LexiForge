using System;
using Godot;

namespace LexiForge.Autoload;

/// <summary>
/// Desktop window fitting for a portrait mobile game.
///
/// The project's 1080x1920 is a DESIGN size, not a monitor resolution. On a
/// laptop the game must open as a smaller portrait window that fits the usable
/// screen area (taskbar / dock / menu bar excluded), centred, and resizable.
/// Android and iOS are deliberately untouched: the OS already supplies a
/// full-screen portrait window and DisplayServer window sizing does not apply.
///
/// project.godot uses canvas_items + "expand" so tall 20:9 phones can reveal
/// 360x800 dp (UI_VISUAL_SYSTEM D-01). On desktop this autoload pins the root
/// Window to ContentScaleAspectEnum.Keep, so get_viewport_rect() stays
/// 1080x1920 at ANY window size. That keeps Tok.Scale (= 3 px per dp) and every
/// cached Tok.Dp() size valid when the player drags the window: no UI rebuild
/// is needed on resize, the whole 360 dp column simply scales uniformly.
/// </summary>
public sealed partial class DisplayFit : Node
{
    private const float DesignAspect = 1080f / 1920f;   // 0.5625, portrait
    private const float ScreenFill = 0.88f;             // breathing room for the title bar
    private const int MinWidth = 270, MinHeight = 480;  // still exactly 9:16
    private const int MaxWidth = 1080, MaxHeight = 1920; // never upscale past the design

    public static DisplayFit Instance { get; private set; } = null!;

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;

        // Mobile: the OS owns the window and "expand" must stay active for D-01.
        if (IsHandheld()) return;

        // Desktop only: pin the logical viewport to the 1080x1920 design size.
        // A non-9:16 manual resize under "expand" would change
        // get_viewport_rect() and leave the cached Tok.Scale / Tok.Dp() sizes
        // stale. "Keep" letterboxes instead, so every resize just scales the
        // whole 360 dp column uniformly and no screen needs rebuilding.
        Window root = GetTree().Root;
        root.ContentScaleSize = new Vector2I(1080, 1920);
        root.ContentScaleMode = Window.ContentScaleModeEnum.CanvasItems;
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Keep;

        CallDeferred(nameof(Fit));  // wait until the main window really exists
    }

    private static bool IsHandheld()
    {
        string os = OS.GetName();
        return os is "Android" or "iOS";
    }

    /// <summary>
    /// Recompute from the current usable screen area. Safe to call again (e.g.
    /// after moving the game to a different monitor via a debug hotkey).
    /// </summary>
    public void Fit()
    {
        Rect2I usable = DisplayServer.ScreenGetUsableRect();
        Vector2I area = usable.Size;
        if (area.X < 1 || area.Y < 1)
            area = DisplayServer.ScreenGetSize();
        if (area.X < 1 || area.Y < 1) return;   // headless / no usable screen

        float maxW = Math.Max(MinWidth, area.X * ScreenFill);
        float maxH = Math.Max(MinHeight, area.Y * ScreenFill);

        // A portrait game on a landscape laptop is constrained by HEIGHT first.
        int h = (int)Math.Round(Math.Min(maxH, MaxHeight));
        int w = (int)Math.Round(h * DesignAspect);

        // Very narrow / unusual displays: fall back to width, then re-derive h.
        if (w > maxW)
        {
            w = (int)Math.Round(Math.Min(maxW, MaxWidth));
            h = (int)Math.Round(w / DesignAspect);
        }

        w = Math.Clamp(w, MinWidth, MaxWidth);
        h = Math.Clamp(h, MinHeight, MaxHeight);

        DisplayServer.WindowSetMinSize(new Vector2I(MinWidth, MinHeight));
        DisplayServer.WindowSetSize(new Vector2I(w, h));

        int x = usable.Position.X + (usable.Size.X - w) / 2;
        int y = usable.Position.Y + (usable.Size.Y - h) / 2;
        DisplayServer.WindowSetPosition(new Vector2I(x, y));

        if (OS.IsDebugBuild())
            GD.Print($"[DisplayFit] window {w}x{h} on usable {area.X}x{area.Y}");
    }
}
