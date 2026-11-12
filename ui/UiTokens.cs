using System.Collections.Generic;
using Godot;

namespace LexiForge.UI;

/// <summary>
/// Design tokens from design/UI_VISUAL_SYSTEM.md section 11.1.
///
/// Every value here is a hex code or a dp figure, never an adjective. Views must
/// read tokens; a `Color` literal outside this file is a bug (section 11.4).
///
/// Units: the design column is 360 dp wide (UI_VISUAL_SYSTEM D-01). The canvas is
/// 1080 px at 3x, so 1 dp = 3 px. <see cref="Scale"/> is derived from the actual
/// viewport width, so a tablet (wider viewport) keeps the same 360 dp column and
/// simply draws it larger. Always convert with <see cref="Dp"/> or
/// <see cref="I"/> - never hard-code pixels in a view.
/// </summary>
public static class Tok
{
    public const float DesignWidthDp = 360f;

    private static float _scale = 3f;

    /// <summary>Pixels per design dp. Set by <see cref="Setup"/> at screen build time.</summary>
    public static float Scale => _scale;

    public static void Setup(float viewportWidthPx)
        => _scale = Mathf.Max(1f, viewportWidthPx / DesignWidthDp);

    public static float Dp(float dp) => dp * _scale;
    public static int I(float dp) => Mathf.RoundToInt(dp * _scale);

    public static Color A(Color c, float alpha) => new(c.R, c.G, c.B, alpha);

    // ---------------------------------------------------------------- surfaces
    public static readonly Color BgVoid = new("#0E0907");
    public static readonly Color BgBase = new("#16100C");
    public static readonly Color Surf1 = new("#1E1712");
    public static readonly Color Surf2 = new("#28201A");
    public static readonly Color Surf3 = new("#35291F");
    public static readonly Color SurfSlot = new("#1B140F");
    public static readonly Color LineHair = new("#2A2019");
    public static readonly Color LineStrong = new("#7F622C");
    public static readonly Color SurfLocked = new("#423630");

    // ----------------------------------------------------------- action/reward
    public static readonly Color Ember = new("#FF7329");
    public static readonly Color EmberPress = new("#E05A14");
    public static readonly Color Gold = new("#FFC752");
    public static readonly Color GoldPale = new("#FFE7B0");
    public static readonly Color GoldDeep = new("#B8862F");

    // ---------------------------------------------------------------- semantic
    public static readonly Color Jade = new("#6FD98C");
    public static readonly Color JadeDim = new("#4E9E66");

    /// <summary>
    /// Heat gradient stops (UI_VISUAL_SYSTEM section 3.6). The ramp is
    /// iron → ember → gold and deliberately never passes through red: a bar that
    /// turns red at the top converts a fluency meter into a countdown and kills
    /// the core pillar (UI-P1 / UI-12).
    /// </summary>
    public static readonly Color HeatIron = new("#423630");
    public static readonly Color HeatWarm = new("#8A4A24");

    /// <summary>Strokes and icons ONLY - fails 4.5:1 as text on a tile.</summary>
    public static readonly Color Rust = new("#C4643F");

    /// <summary>Text ONLY - 5.66:1 on Surf2.</summary>
    public static readonly Color RustText = new("#D98461");
    public static readonly Color Locked = new("#423630");

    // -------------------------------------------------------------------- text
    public static readonly Color Text = new("#F2E6D6");
    public static readonly Color TextDim = new("#CDBBA7");
    public static readonly Color Muted = new("#A19182");
    public static readonly Color Ink = new("#1A1108");

    // ---------------------------------------------------------------- geometry
    public const float RackTileDp = 84f, SlotDp = 64f, KeystoneDp = 68f;
    public const float BtnPrimaryDp = 72f, BtnSecondaryDp = 56f, ChipDp = 32f;
    public const float BannerReserveDp = 56f, BannerGapDp = 8f, BottomInsetDp = 24f;
    public const float RadiusSlotDp = 12f, RadiusKeystoneDp = 14f, RadiusBtnDp = 18f, RadiusCardDp = 20f;
    public const float MarginDp = 24f;
    public const float GapDp = 8f;

    // ------------------------------------------------------------------ motion
    public const float TPress = 0.080f, TFly = 0.220f, TLock = 0.120f;
    public const float TReject = 0.090f, TFlow = 0.160f, TDrain = 0.300f;
    public const float TCard = 0.260f, TKey = 0.520f, TStagger = 0.018f;
    public const float TSheet = 0.220f, TChip = 0.320f;

    /// <summary>Loud budget (UI-01): one loud event per 800 ms, and only the keystone may use it.</summary>
    public const float LoudWindow = 0.800f;

    // -------------------------------------------------------------------- type
    // Sizes are sp, which equals dp at this canvas scale. No text below 20 sp (UI-14).
    public const float SpDisplay = 34f, SpH1 = 30f, SpH2 = 24f, SpBody = 22f, SpLabel = 20f, SpMeta = 20f;
    public const float SpTileSlot = 30f, SpTileRack = 34f, SpNumeralL = 34f, SpNumeralM = 30f;
}

/// <summary>
/// Responsive ladder (UI_VISUAL_SYSTEM section 5.4). Values are dp; the vertical
/// space is DISTRIBUTED, never stretched: the Read band anchors top, the Act band
/// anchors bottom, and the Solve band absorbs the variance (D-01).
/// </summary>
public readonly struct Layout
{
    public readonly float HeightDp;
    public readonly float RackTileDp;
    public readonly float SlotDp;
    public readonly float ComposeDp;
    public readonly float ActionDp;
    public readonly bool Tablet;

    private Layout(float heightDp, float rack, float slot, float compose, float action, bool tablet)
    {
        HeightDp = heightDp;
        RackTileDp = rack;
        SlotDp = slot;
        ComposeDp = compose;
        ActionDp = action;
        Tablet = tablet;
    }

    public static Layout For(float viewportHeightPx, float viewportWidthPx)
    {
        float heightDp = viewportHeightPx / Tok.Scale;
        float widthDp = viewportWidthPx / Tok.Scale;

        if (widthDp >= 600f) return new Layout(heightDp, 96f, 76f, 48f, 80f, true);
        if (heightDp < 700f) return new Layout(heightDp, 74f, 54f, 36f, 64f, false);
        if (heightDp < 760f) return new Layout(heightDp, 80f, 60f, 40f, 68f, false);
        return new Layout(heightDp, 84f, 64f, 44f, 72f, false);
    }
}

/// <summary>
/// Optional type families (UI_VISUAL_SYSTEM section 4.1). The project ships no
/// font binaries, so each lookup is best-effort: if the OFL face is not present
/// under res://fonts, the engine's default font is used and the layout still
/// holds. Drop Fredoka-SemiBold.ttf / Fraunces-SemiBold.ttf /
/// AtkinsonHyperlegible-Bold.ttf into res://fonts to activate the intended
/// typography without touching a single view.
/// </summary>
public static class FontBook
{
    private const string TilePath = "res://fonts/Fredoka-SemiBold.ttf";
    private const string DisplayPath = "res://fonts/Fraunces-SemiBold.ttf";
    private const string AccessPath = "res://fonts/AtkinsonHyperlegible-Bold.ttf";

    private static readonly Dictionary<string, Font?> Cache = new();

    private static Font? TryLoad(string path)
    {
        if (Cache.TryGetValue(path, out Font? cached)) return cached;
        Font? font = null;
        if (ResourceLoader.Exists(path))
        {
            font = ResourceLoader.Load<Font>(path);
            if (font is null) GD.PushWarning($"[FontBook] {path} exists but is not a Font.");
        }
        Cache[path] = font;
        return font;
    }

    /// <summary>Rack and grid letters: geometric-rounded, high x-height.</summary>
    public static Font Tile(Control ctx, bool dyslexiaFriendly)
    {
        if (dyslexiaFriendly)
            return TryLoad(AccessPath) ?? TryLoad(TilePath) ?? ctx.GetThemeDefaultFont();
        return TryLoad(TilePath) ?? ctx.GetThemeDefaultFont();
    }

    /// <summary>Wordmark and the four-per-session headlines.</summary>
    public static Font Display(Control ctx) => TryLoad(DisplayPath) ?? ctx.GetThemeDefaultFont();
}
