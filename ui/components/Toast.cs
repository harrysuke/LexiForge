using Godot;

namespace LexiForge.UI;

/// <summary>
/// Single pooled toast (UI_DESIGN section 6). Never more than one on screen, never
/// over the rack, never over the Act band.
///
/// The default band is y = 180 dp, but the forge re-places it directly above the
/// Act band via <see cref="PlaceAbove"/>: on the Compact breakpoint the Solve band
/// starts at 148 dp, so a fixed 180 dp pill lands on top of the first grid row and
/// hides the letter the player just forged.
///
/// A second call replaces the first with a 120 ms crossfade instead of stacking -
/// two toasts at once is the beginning of noise, and noise is the opposite of
/// reward.
/// </summary>
public sealed partial class Toast : Control
{
    private PanelContainer _pill = null!;
    private Label _label = null!;
    private double _remaining;

    /// <summary>Last text shown. Read by DevSmoke to assert rejection copy (UI-66).</summary>
    public string CurrentText => _label?.Text ?? "";

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.TopWide);
        OffsetLeft = 0;
        OffsetRight = 0;
        OffsetTop = Tok.Dp(180f);
        OffsetBottom = Tok.Dp(180f) + Tok.Dp(84f);
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(centre);
        Ui.FullRect(centre);

        _pill = new PanelContainer { MouseFilter = MouseFilterEnum.Ignore, Visible = false };
        StyleBoxFlat pill = Ui.Box(Tok.A(Tok.Surf1, 0.96f), 16f);
        // 4 dp vertical padding, not 6: the gutter between the last grid row and the
        // rack is ~49 dp, and every dp the pill gives up is a dp it stops covering
        // the slot the player just filled.
        pill.ContentMarginTop = Tok.Dp(4f);
        pill.ContentMarginBottom = Tok.Dp(4f);
        _pill.AddThemeStyleboxOverride("panel", pill);
        centre.AddChild(_pill);

        _label = Ui.MakeLabel("", 24f, Tok.Text, HorizontalAlignment.Center, wrap: true);
        _label.CustomMinimumSize = new Vector2(Tok.Dp(260f), 0);
        _pill.AddChild(_label);
        SetProcess(true);
    }

    /// <summary>
    /// Pins the pill's band so its bottom edge sits <paramref name="bottomInsetPx"/>
    /// above the bottom of the screen. The forge passes the Act band height, which
    /// puts the toast in the empty gutter above the rack instead of over the grid.
    /// </summary>
    public void PlaceAbove(float bottomInsetPx)
    {
        SetAnchorsPreset(LayoutPreset.BottomWide);
        OffsetLeft = 0;
        OffsetRight = 0;
        OffsetBottom = -bottomInsetPx;
        // Band height = the pill's own height plus 4 dp. A 72 dp band centred the
        // pill 30 dp higher, which put it across the bottom row of the grid.
        OffsetTop = -bottomInsetPx - Tok.Dp(44f);
    }

    public void Show(string text, Color color)    {
        if (_label is null) return;
        bool replacing = _pill.Visible;
        _label.Text = text;
        _label.AddThemeColorOverride("font_color", color);
        _pill.Visible = true;
        _pill.Modulate = new Color(1, 1, 1, replacing ? 0f : 1f);

        // Rise 8 dp + fade in 180 ms, hold 1.4 s, exit 200 ms.
        Tween tween = UiAnim.On(_pill, "toast");
        tween.SetParallel(true);
        tween.TweenProperty(_pill, "modulate:a", 1.0f, UiAnim.Dur(replacing ? 0.12f : 0.18f));
        _remaining = 1.4 + 0.18;
    }

    public override void _Process(double delta)
    {
        if (_remaining <= 0) return;
        _remaining -= delta;
        if (_remaining > 0) return;
        _remaining = 0;
        Tween tween = UiAnim.On(_pill, "toast");
        tween.TweenProperty(_pill, "modulate:a", 0.0f, UiAnim.Dur(0.2f));
        tween.TweenCallback(Callable.From(() => _pill.Visible = false));
    }
}
