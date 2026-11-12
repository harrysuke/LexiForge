using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>Button variants from UI_VISUAL_SYSTEM section 6.3.</summary>
public enum BtnKind { Primary, Secondary, Ghost, Icon, DangerNeutral }

/// <summary>
/// Allocation-light UI helpers. Views are still built from Control nodes in code
/// so the .tscn files stay tiny and there are no fragile node-path strings.
///
/// Every size this class accepts is in **sp / dp** and is converted through
/// <see cref="Tok.Dp"/>. Nothing here hard-codes a pixel: that is what makes the
/// 360 dp column hold from a 640 dp 16:9 panel to a tablet.
///
/// Palette aliases exist so views can say `Ui.Ember` instead of a hex literal;
/// the values all come from <see cref="Tok"/> (section 11.4: no Color literal
/// outside UiTokens.cs).
/// </summary>
public static class Ui
{
    // Surfaces
    public static readonly Color Bg = Tok.BgBase;
    public static readonly Color BgVoid = Tok.BgVoid;
    public static readonly Color Panel = Tok.Surf1;
    public static readonly Color PanelRaised = Tok.Surf2;
    public static readonly Color PanelHigh = Tok.Surf3;
    public static readonly Color Slot = Tok.SurfSlot;
    public static readonly Color Line = Tok.LineHair;
    public static readonly Color LineStrong = Tok.LineStrong;
    public static readonly Color Locked = Tok.SurfLocked;

    // Action / reward
    public static readonly Color Ember = Tok.Ember;
    public static readonly Color EmberPress = Tok.EmberPress;
    public static readonly Color Gold = Tok.Gold;
    public static readonly Color GoldPale = Tok.GoldPale;
    public static readonly Color GoldDeep = Tok.GoldDeep;

    // Semantic. `Good`/`Bad` are kept as names because older views read them;
    // they now resolve to the desaturated pair the spec requires (UI-10, C-2).
    public static readonly Color Good = Tok.Jade;
    public static readonly Color Bad = Tok.RustText;
    public static readonly Color Rust = Tok.Rust;
    public static readonly Color RustText = Tok.RustText;

    // Text
    public static readonly Color Text = Tok.Text;
    public static readonly Color TextDim = Tok.TextDim;
    public static readonly Color Muted = Tok.Muted;
    public static readonly Color Ink = Tok.Ink;

    public static void FullRect(Control c)
    {
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        c.SetOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    /// <summary>Fills the parent on both axes with a fixed dp height and bottom anchor.</summary>
    public static void AnchorBottom(Control c, float heightDp)
    {
        c.SetAnchorsPreset(Control.LayoutPreset.BottomWide);
        c.CustomMinimumSize = new Vector2(0, Tok.Dp(heightDp));
        // Offsets alone define the rect once the anchors are set. Assigning Size as
        // well makes Godot warn ("non-equal opposite anchors will have their size
        // overridden after _ready") and then override it - the assignment is a lie.
        c.OffsetTop = -Tok.Dp(heightDp);
        c.OffsetBottom = 0;
        c.OffsetLeft = 0;
        c.OffsetRight = 0;
    }

    public static StyleBoxFlat Box(Color color, float radiusDp = 18f)
    {
        var box = new StyleBoxFlat { BgColor = color };
        box.SetCornerRadiusAll(Tok.I(radiusDp));
        box.ContentMarginLeft = Tok.Dp(12);
        box.ContentMarginRight = Tok.Dp(12);
        box.ContentMarginTop = Tok.Dp(6);
        box.ContentMarginBottom = Tok.Dp(6);
        return box;
    }

    public static StyleBoxFlat StrokeBox(Color fill, Color stroke, float radiusDp, float strokeDp)
    {
        StyleBoxFlat box = Box(fill, radiusDp);
        box.BorderColor = stroke;
        int width = Tok.I(strokeDp);
        box.BorderWidthLeft = width;
        box.BorderWidthRight = width;
        box.BorderWidthTop = width;
        box.BorderWidthBottom = width;
        return box;
    }

    /// <summary>
    /// A quiet scrollbar: 3 dp grabber in Gold @ 30%, no track.
    ///
    /// The default theme draws a wide, near-white bar with a sunken track, which on
    /// the draft screen is louder than the rune cards it is supposed to sit behind.
    /// A hairline grabber is still a position indicator - removing it entirely would
    /// cost the player the only cue for how much list is left.
    /// </summary>
    public static void StyleScroll(ScrollContainer scroll)
    {
        VScrollBar bar = scroll.GetVScrollBar();
        bar.CustomMinimumSize = new Vector2(Tok.Dp(3f), 0);
        bar.AddThemeStyleboxOverride("scroll", new StyleBoxEmpty());
        bar.AddThemeStyleboxOverride("grabber", Box(Tok.A(Tok.Gold, 0.30f), 2f));
        bar.AddThemeStyleboxOverride("grabber_highlight", Box(Tok.A(Tok.Gold, 0.55f), 2f));
        bar.AddThemeStyleboxOverride("grabber_pressed", Box(Tok.A(Tok.Gold, 0.55f), 2f));
    }

    /// <summary>
    /// Draws a rounded rect straight into a CanvasItem. Cheaper than instancing a
    /// Panel per cell, and it keeps a custom `_Draw` (tiles, slots, bars) free of
    /// child nodes.
    /// </summary>
    public static void FillRoundRect(CanvasItem canvas, Rect2 rect, float radiusDp, Color color)
    {
        if (rect.Size.X <= 0 || rect.Size.Y <= 0 || color.A <= 0) return;
        var box = new StyleBoxFlat { BgColor = color };
        box.SetCornerRadiusAll(Tok.I(radiusDp));
        canvas.DrawStyleBox(box, rect);
    }

    public static void StrokeRoundRect(CanvasItem canvas, Rect2 rect, float radiusDp, Color color, float widthDp)
    {
        if (rect.Size.X <= 0 || rect.Size.Y <= 0 || color.A <= 0) return;
        var box = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0), BorderColor = color };
        box.SetCornerRadiusAll(Tok.I(radiusDp));
        int width = Mathf.Max(1, Tok.I(widthDp));
        box.BorderWidthLeft = width;
        box.BorderWidthRight = width;
        box.BorderWidthTop = width;
        box.BorderWidthBottom = width;
        canvas.DrawStyleBox(box, rect);
    }

    public static Label MakeLabel(string text, float sp = 22f, Color? color = null,
                                  HorizontalAlignment align = HorizontalAlignment.Center,
                                  bool wrap = false)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = wrap ? TextServer.AutowrapMode.WordSmart : TextServer.AutowrapMode.Off
        };
        label.AddThemeFontSizeOverride("font_size", Tok.I(sp));
        label.AddThemeColorOverride("font_color", color ?? Text);
        return label;
    }

    /// <summary>Sets the display face (Fraunces when shipped) on a label.</summary>
    public static Label Display(Label label, Control ctx)
    {
        label.AddThemeFontOverride("font", FontBook.Display(ctx));
        return label;
    }

    /// <summary>
    /// Section 6.3 buttons. `heightDp` defaults to the variant's own height.
    /// Pressed feedback (scale 0.96 + light haptic) is wired here so no view can
    /// forget it: "a button that does not move when pressed is a button the
    /// player presses twice."
    /// </summary>
    public static Button MakeButton(string text, Action onPressed, BtnKind kind = BtnKind.Secondary,
                                    float heightDp = 0f, float fontSizeSp = 0f, string? tapCue = "ui.tap")
    {
        float height = heightDp > 0f ? heightDp : kind switch
        {
            BtnKind.Primary => Tok.BtnPrimaryDp,
            BtnKind.Icon => Tok.BtnSecondaryDp,
            BtnKind.Ghost => 44f,
            _ => Tok.BtnSecondaryDp
        };
        float fontSize = fontSizeSp > 0f ? fontSizeSp : kind switch
        {
            BtnKind.Primary => 24f,
            BtnKind.Ghost => 20f,
            _ => 22f
        };

        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, Tok.Dp(height)),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            // No ClipText and no overrun trimming here. Both shrink a text button's
            // minimum width to a handful of pixels, which silently erased every
            // ShrinkBegin label ("Ad privacy: not set" rendered as a 60 px stub).
            // Multi-line copy belongs in MakeCta; one-line copy stays measurable.
            ClipText = false
        };
        button.AddThemeFontSizeOverride("font_size", Tok.I(fontSize));
        Style(button, kind);

        button.ButtonDown += () =>
        {
            UiAnim.PressFeedback(button, true);
            UiAnim.Haptic(12);
        };
        button.ButtonUp += () => UiAnim.PressFeedback(button, false);

        button.Pressed += () =>
        {
            // Cue BEFORE the action: the tap must start on the input frame,
            // before onPressed() navigates, opens a panel or awaits an ad.
            if (tapCue is not null) EventBus.Cue(tapCue);
            onPressed();
        };
        return button;
    }

    /// <summary>Re-skins an existing button after a state change (kind switch).</summary>
    public static void Style(Button button, BtnKind kind)
    {
        float radius = kind == BtnKind.Icon ? 999f : Tok.RadiusBtnDp;
        switch (kind)
        {
            case BtnKind.Primary:
            {
                StyleBoxFlat normal = Box(Tok.Ember, radius);
                // A 5 dp EmberPress band under the fill reads as the gradient
                // bottom without needing a shader (C-1 fix: Ink label = 6.86:1).
                normal.BorderColor = Tok.EmberPress;
                normal.BorderWidthBottom = Tok.I(5f);
                normal.BorderWidthTop = Tok.I(1f);
                normal.BorderColor = Tok.EmberPress;
                StyleBoxFlat hover = Box(Tok.Ember, radius);
                StyleBoxFlat pressed = Box(Tok.EmberPress, radius);
                button.AddThemeStyleboxOverride("normal", normal);
                button.AddThemeStyleboxOverride("hover", hover);
                button.AddThemeStyleboxOverride("pressed", pressed);
                // Disabled is a SOLID, duller surface, not 40%-alpha Ember: a
                // translucent ember over a near-black page composites to a muddy
                // brown that reads as a different colour rather than a dimmed one
                // (UI_VISUAL_SYSTEM 7.2 - "must not read as a broken promise").
                StyleBoxFlat off = StrokeBox(Tok.Surf3, Tok.LineHair, radius, 1f);
                button.AddThemeStyleboxOverride("disabled", off);
                button.AddThemeColorOverride("font_color", Tok.Ink);
                button.AddThemeColorOverride("font_hover_color", Tok.Ink);
                button.AddThemeColorOverride("font_pressed_color", Tok.Ink);
                button.AddThemeColorOverride("font_disabled_color", Tok.Muted);
                break;
            }
            case BtnKind.Ghost:
            {
                StyleBoxFlat normal = Box(new Color(0, 0, 0, 0), radius);
                normal.ContentMarginLeft = Tok.Dp(10);
                normal.ContentMarginRight = Tok.Dp(10);
                button.AddThemeStyleboxOverride("normal", normal);
                button.AddThemeStyleboxOverride("hover", Box(Tok.A(Tok.Surf2, 0.6f), radius));
                button.AddThemeStyleboxOverride("pressed", Box(Tok.Surf2, radius));
                button.AddThemeStyleboxOverride("disabled", Box(new Color(0, 0, 0, 0), radius));
                button.AddThemeColorOverride("font_color", Tok.Muted);
                button.AddThemeColorOverride("font_hover_color", Tok.TextDim);
                button.AddThemeColorOverride("font_pressed_color", Tok.Text);
                button.AddThemeColorOverride("font_disabled_color", Tok.A(Tok.Muted, 0.4f));
                break;
            }
            case BtnKind.Icon:
            {
                StyleBoxFlat normal = Box(Tok.Surf1, radius);
                StyleBoxFlat pressed = Box(Tok.Surf3, radius);
                normal.ContentMarginLeft = 0;
                normal.ContentMarginRight = 0;
                pressed.ContentMarginLeft = 0;
                pressed.ContentMarginRight = 0;
                button.AddThemeStyleboxOverride("normal", normal);
                button.AddThemeStyleboxOverride("hover", Box(Tok.Surf2, radius));
                button.AddThemeStyleboxOverride("pressed", pressed);
                button.AddThemeStyleboxOverride("disabled", Dim(Tok.Surf1, 0.5f));
                button.AddThemeColorOverride("font_color", Tok.Gold);
                button.AddThemeColorOverride("font_disabled_color", Tok.A(Tok.Gold, 0.35f));
                break;
            }
            case BtnKind.DangerNeutral:
            {
                button.AddThemeStyleboxOverride("normal",
                    StrokeBox(Tok.Surf1, Tok.A(Tok.Rust, 0.4f), radius, 1f));
                button.AddThemeStyleboxOverride("hover", Box(Tok.Surf2, radius));
                button.AddThemeStyleboxOverride("pressed", Box(Tok.Surf3, radius));
                button.AddThemeStyleboxOverride("disabled", Dim(Tok.Surf1, 0.5f));
                button.AddThemeColorOverride("font_color", Tok.RustText);
                button.AddThemeColorOverride("font_hover_color", Tok.RustText);
                button.AddThemeColorOverride("font_pressed_color", Tok.RustText);
                button.AddThemeColorOverride("font_disabled_color", Tok.A(Tok.RustText, 0.4f));
                break;
            }
            default:
            {
                button.AddThemeStyleboxOverride("normal", StrokeBox(Tok.Surf1, Tok.LineHair, radius, 1f));
                button.AddThemeStyleboxOverride("hover", Box(Tok.Surf2, radius));
                button.AddThemeStyleboxOverride("pressed", Box(Tok.Surf3, radius));
                button.AddThemeStyleboxOverride("disabled", Dim(Tok.Surf1, 0.55f));
                button.AddThemeColorOverride("font_color", Tok.Text);
                button.AddThemeColorOverride("font_hover_color", Tok.Text);
                button.AddThemeColorOverride("font_pressed_color", Tok.TextDim);
                button.AddThemeColorOverride("font_disabled_color", Tok.A(Tok.Muted, 0.5f));
                break;
            }
        }
    }

    private static StyleBoxFlat Dim(Color color, float alpha)
    {
        StyleBoxFlat box = Box(Tok.A(color, alpha), 18f);
        return box;
    }

    /// <summary>
    /// A two-line call to action (UI-21/UI-22): the verb, then the *state*. The
    /// subtitle is what removes a decision, so it is part of the same target.
    /// </summary>
    public static Button MakeCta(string title, string subtitle, Action onPressed,
                                 float heightDp = 132f, string? tapCue = null,
                                 BtnKind kind = BtnKind.Primary, Color? labelColor = null)
    {
        Button button = MakeButton("", onPressed, kind, heightDp, 24f, tapCue);
        Color ink = labelColor ?? Tok.Ink;
        button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;

        var column = new VBoxContainer
        {
            MouseFilter = Control.MouseFilterEnum.Ignore,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill
        };
        column.AddThemeConstantOverride("separation", Tok.I(2f));
        column.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        column.OffsetLeft = Tok.Dp(16);
        column.OffsetRight = -Tok.Dp(16);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        button.AddChild(column);

        Label head = MakeLabel(title, 24f, ink);
        head.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(head);

        Label sub = MakeLabel(subtitle, 20f, Tok.A(ink, 0.7f));
        sub.MouseFilter = Control.MouseFilterEnum.Ignore;
        column.AddChild(sub);
        return button;
    }

    /// <summary>Updates the two lines of a CTA built by <see cref="MakeCta"/>.</summary>
    public static void SetCtaText(Button cta, string title, string subtitle)
    {
        if (cta.GetChildCount() == 0 || cta.GetChild(0) is not VBoxContainer column) return;
        if (column.GetChildCount() < 2) return;
        if (column.GetChild(0) is Label head) head.Text = title;
        if (column.GetChild(1) is Label sub) sub.Text = subtitle;
    }

    public static PanelContainer MakePanel(Color? color = null, float radiusDp = Tok.RadiusCardDp)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(color ?? Tok.Surf1, radiusDp));
        return panel;
    }

    /// <summary>Builds a full-rect, safe-area-margined VBox column on the given root.</summary>
    public static VBoxContainer AttachColumn(Control root, float separationDp = 20f, float marginDp = 24f)
    {
        var frame = new MarginContainer();
        frame.AddThemeConstantOverride("margin_left", Tok.I(marginDp));
        frame.AddThemeConstantOverride("margin_right", Tok.I(marginDp));
        frame.AddThemeConstantOverride("margin_top", Tok.I(marginDp));
        frame.AddThemeConstantOverride("margin_bottom", Tok.I(marginDp));

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(separationDp));
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        frame.AddChild(column);

        root.AddChild(frame);
        FullRect(frame);
        return column;
    }

    public static HBoxContainer MakeRow(float separationDp = 8f)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", Tok.I(separationDp));
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return row;
    }

    /// <summary>Vertical spacer that absorbs leftover space in a VBox.</summary>
    public static Control Spacer(float minDp = 0f)
        => new() { SizeFlagsVertical = Control.SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, Tok.Dp(minDp)) };

    public static Control FixedSpacer(float heightDp)
        => new() { CustomMinimumSize = new Vector2(0, Tok.Dp(heightDp)), SizeFlagsVertical = Control.SizeFlags.ShrinkBegin };

    /// <summary>
    /// Baseline-centred text for custom `_Draw` code. Centres the em box and then
    /// nudges up slightly so a capital letter sits optically centred - a Label
    /// would centre the line box and leave letters 2-3 dp low (section 4.3).
    /// </summary>
    public static void DrawTextCentred(CanvasItem canvas, Font font, string text, Rect2 rect,
                                       float sp, Color color)
    {
        if (string.IsNullOrEmpty(text)) return;
        int size = Tok.I(sp);
        Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
        float baseline = rect.Position.Y + (rect.Size.Y - measured.Y) * 0.5f + font.GetAscent(size)
                         - (font.GetAscent(size) - font.GetDescent(size)) * 0.5f;
        var origin = new Vector2(rect.Position.X + (rect.Size.X - measured.X) * 0.5f, baseline);
        canvas.DrawString(font, origin, text, HorizontalAlignment.Left, -1, size, color);
    }

    /// <summary>One line of text with a trailing glyph, e.g. "1,240" + a coin mark.</summary>
    public static HBoxContainer MakeMetaRow(float separationDp = 6f)
    {
        HBoxContainer row = MakeRow(separationDp);
        row.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd;
        return row;
    }

    /// <summary>Tabular figures keep a rolling number from jittering horizontally (section 4.3).</summary>
    public static Label MakeNumeral(string text, float sp, Color color, HorizontalAlignment align = HorizontalAlignment.Right)
    {
        Label label = MakeLabel(text, sp, color, align);
        return label;
    }
}
