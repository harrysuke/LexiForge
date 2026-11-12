using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// Small, allocation-light UI helpers. All views are built from Control nodes in
/// code so the .tscn files stay tiny and there are no fragile node-path strings.
/// Components are still separate, independently instancable SCENES; this kit
/// only builds their contents.
///
/// Palette: warm "Emberworks" forge tones. Fixed colours keep draw calls low and
/// avoid shipping a theme resource for the P0 slice.
/// </summary>
public static class Ui
{
    public static readonly Color Bg = new(0.086f, 0.063f, 0.047f, 1f);
    public static readonly Color Panel = new(0.145f, 0.106f, 0.080f, 1f);
    public static readonly Color PanelRaised = new(0.196f, 0.141f, 0.102f, 1f);
    public static readonly Color Ember = new(1.0f, 0.45f, 0.16f, 1f);
    public static readonly Color Gold = new(1.0f, 0.78f, 0.32f, 1f);
    public static readonly Color Text = new(0.95f, 0.90f, 0.84f, 1f);
    public static readonly Color Muted = new(0.63f, 0.57f, 0.51f, 1f);
    public static readonly Color Good = new(0.45f, 0.85f, 0.55f, 1f);
    public static readonly Color Bad = new(0.95f, 0.38f, 0.34f, 1f);
    public static readonly Color Locked = new(0.26f, 0.21f, 0.18f, 1f);

    public static void FullRect(Control c)
    {
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        c.SetOffsetsPreset(Control.LayoutPreset.FullRect);
    }

    public static StyleBoxFlat Box(Color color, int radius = 18)
    {
        var box = new StyleBoxFlat { BgColor = color };
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = 16;
        box.ContentMarginRight = 16;
        box.ContentMarginTop = 10;
        box.ContentMarginBottom = 10;
        return box;
    }

    public static Label MakeLabel(string text, int size = 34, Color? color = null,
                                  HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color ?? Text);
        return label;
    }

    /// <param name="tapCue">
    /// Semantic feedback cue for this button. Pass null when the button emits
    /// its OWN cue (Shuffle, Undo, a panel open/close) so two sounds never fire
    /// on one tap. Views never name a bus or an audio file - see
    /// AUDIO_DESIGN.md section 4.
    /// </param>
    public static Button MakeButton(string text, Action onPressed, Color? background = null,
                                    int minHeight = 108, int fontSize = 34,
                                    string? tapCue = "ui.tap")
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, minHeight),
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", Text);
        button.AddThemeColorOverride("font_hover_color", Gold);
        button.AddThemeColorOverride("font_pressed_color", Ember);
        button.AddThemeStyleboxOverride("normal", Box(background ?? PanelRaised));
        button.AddThemeStyleboxOverride("hover", Box(Ember));
        button.AddThemeStyleboxOverride("pressed", Box(Gold));
        button.AddThemeStyleboxOverride("disabled", Box(Locked));
        button.Pressed += () =>
        {
            // Cue BEFORE the action: the tap must start on the input frame,
            // before onPressed() navigates, opens a panel or awaits an ad.
            // Emitting through EventBus.Cue keeps this view audio-agnostic.
            if (tapCue is not null) EventBus.Cue(tapCue);
            onPressed();
        };
        return button;
    }

    public static PanelContainer MakePanel(Color? color = null, int radius = 22)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", Box(color ?? Panel, radius));
        return panel;
    }

    /// <summary>Builds a full-rect, safe-area-margined VBox column on the given root.</summary>
    public static VBoxContainer AttachColumn(Control root, int separation = 20, int margin = 56)
    {
        var frame = new MarginContainer();
        frame.AddThemeConstantOverride("margin_left", margin);
        frame.AddThemeConstantOverride("margin_right", margin);
        frame.AddThemeConstantOverride("margin_top", margin);
        frame.AddThemeConstantOverride("margin_bottom", margin);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", separation);
        column.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        column.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        frame.AddChild(column);

        root.AddChild(frame);
        FullRect(frame);
        return column;
    }

    public static HBoxContainer MakeRow(int separation = 16)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", separation);
        row.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return row;
    }
}
