using Godot;

namespace LexiForge.UI;

/// <summary>
/// The Ad Safe Frame (UI_VISUAL_SYSTEM D-02 / section 5.3).
///
/// v1 ships NO banner (GDD 8.1), but the frame is designed and reserved anyway.
/// A designer who says "banners are out of scope" ships a layout that breaks the
/// day a banner appears; a designer who reserves the frame has already solved it.
///
/// The reserve costs 0 dp while disabled because <see cref="Rect"/> returns an
/// empty rect, so the debug assertion below is free in the shipped build.
/// </summary>
public static class AdSafeFrame
{
    /// <summary>
    /// Flip to true only when a mediation partner mandates a banner.
    /// Deliberately `static readonly` and not `const`: as a compile-time constant the
    /// two early returns below are provably dead code and the compiler warns on every
    /// build, which trains people to ignore warnings. Same one-line flip, same zero
    /// cost (an empty Rect2), no dead-code noise.
    /// </summary>
    public static readonly bool BannerEnabled = false;

    public static Rect2 Rect(Rect2 screen)
    {
        if (!BannerEnabled) return new Rect2();
        float height = Tok.Dp(Tok.BannerReserveDp);
        float gap = Tok.Dp(Tok.BannerGapDp);
        float bottom = screen.Size.Y - Tok.Dp(Tok.BottomInsetDp);
        return new Rect2(0, bottom - height - gap, screen.Size.X, height + gap);
    }

    /// <summary>
    /// Debug-only walk: asserts that no interactive Control intersects the reserve.
    /// An overlap becomes a console error in the editor instead of a Play policy
    /// complaint in production.
    /// </summary>
    public static int AssertClear(Control root)
    {
        if (!BannerEnabled) return 0;
        Rect2 safe = Rect(root.GetViewportRect());
        int violations = 0;
        Walk(root, safe, ref violations);
        if (violations > 0)
            GD.PushError($"[AdSafeFrame] {violations} interactive control(s) intersect the banner reserve.");
        return violations;
    }

    private static void Walk(Node node, Rect2 safe, ref int violations)
    {
        if (node is Control control && control.Visible)
        {
            bool interactive = control.MouseFilter != Control.MouseFilterEnum.Ignore;
            if (interactive && control.GetGlobalRect().Intersects(safe))
            {
                violations++;
                GD.PushError($"[AdSafeFrame] {control.GetPath()} overlaps the banner reserve.");
            }
        }
        foreach (Node child in node.GetChildren()) Walk(child, safe, ref violations);
    }
}
