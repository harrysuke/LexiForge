using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// In-game audio settings (design/AUDIO_DESIGN.md section 8).
///
/// Reachable from the pause menu on every screen, so a player never has to
/// abandon a run to change the mix. The panel is a child of the hosting
/// PauseOverlay: it is pooled with the screen, it renders above the pause card,
/// and it inherits the overlay's ProcessMode.Always so it keeps working while
/// GetTree().Paused is true.
///
/// This class talks ONLY to AudioDirector.ApplySettings / PreviewSettings. It
/// never touches a bus, a dB value or a stream - the view layer stays
/// audio-agnostic (AUDIO_DESIGN.md section 3.1).
///
/// Sliders preview live but persist only on DragEnded: ApplySettings flushes the
/// save file, and doing that on every ValueChanged would write once per pixel.
/// </summary>
public sealed partial class AudioSettingsPanel : Control
{
    private HSlider _music = null!;
    private HSlider _sfx = null!;
    private HSlider _ambience = null!;
    private Label _musicValue = null!;
    private Label _sfxValue = null!;
    private Label _ambienceValue = null!;
    private CheckButton _mute = null!;
    private CheckButton _reduced = null!;
    private CheckButton _haptics = null!;
    private bool _loading;

    private AudioSettingsPanel()
    {
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    /// <summary>Creates and attaches one panel per hosting overlay.</summary>
    public static AudioSettingsPanel AttachTo(Control host)
    {
        var panel = new AudioSettingsPanel();
        host.AddChild(panel);
        return panel;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Ui.FullRect(this);

        var dim = new ColorRect { Color = Tok.A(Tok.BgVoid, 0.92f) };
        AddChild(dim);
        Ui.FullRect(dim);

        var centre = new CenterContainer();
        AddChild(centre);
        Ui.FullRect(centre);

        var card = Ui.MakePanel(Tok.Surf1, Tok.RadiusCardDp);
        card.CustomMinimumSize = new Vector2(Tok.Dp(320f), 0);
        centre.AddChild(card);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(10f));
        card.AddChild(column);

        column.AddChild(Ui.MakeLabel("SOUND", 24f, Tok.Text, HorizontalAlignment.Left));
        // Deliberately NOT autowrapped: a wrapped Label reports a minimum height
        // measured at width 0, which made this card 10538 px tall and pushed it off
        // the screen. Keep every line inside the 320 dp card instead.
        column.AddChild(Ui.MakeLabel("All optional — playable muted.", 20f, Tok.Muted, HorizontalAlignment.Left));

        _music = AddSlider(column, "Music", out _musicValue);
        _sfx = AddSlider(column, "Effects", out _sfxValue);
        _ambience = AddSlider(column, "Ambience", out _ambienceValue);

        _mute = AddToggle(column, "Mute all");
        _reduced = AddToggle(column, "Reduce sound density");
        _haptics = AddToggle(column, "Haptics (Android)");

        column.AddChild(Ui.MakeButton("Done", Close, BtnKind.Primary, 56f, 22f, null));

        // Sliders: preview while dragging, persist when the finger lifts.
        _music.ValueChanged += _ => OnSliderMoved();
        _sfx.ValueChanged += _ => OnSliderMoved();
        _ambience.ValueChanged += _ => OnSliderMoved();
        _music.DragEnded += _ => OnSliderCommitted();
        _sfx.DragEnded += _ => OnSliderCommitted();
        _ambience.DragEnded += _ => OnSliderCommitted();

        // Switches: immediate effect, so they audition themselves.
        _mute.Toggled += _ => OnToggleChanged();
        _reduced.Toggled += _ => OnToggleChanged();
        _haptics.Toggled += _ => OnToggleChanged();
    }

    public bool IsOpen => Visible;

    public void Open()
    {
        if (Visible) return;
        SyncFromDirector();
        Visible = true;
        MoveToFront();
        EventBus.Cue("ui.open");
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        EventBus.Cue("ui.close");
    }

    // --------------------------------------------------------------- internals
    private void SyncFromDirector()
    {
        AudioDirector? audio = AudioDirector.Instance;
        if (audio is null) return;

        // _loading suppresses the handlers: assigning Value/ButtonPressed emits
        // ValueChanged/Toggled, which would otherwise write the save on open.
        _loading = true;
        _music.Value = Math.Round(audio.MusicVolume * 100.0);
        _sfx.Value = Math.Round(audio.SfxVolume * 100.0);
        _ambience.Value = Math.Round(audio.AmbienceVolume * 100.0);
        _mute.SetPressedNoSignal(audio.Muted);
        _reduced.SetPressedNoSignal(audio.ReducedSoundDensity);
        _haptics.SetPressedNoSignal(audio.HapticsEnabled);
        _loading = false;
        RefreshValueLabels();
    }

    private void OnSliderMoved()
    {
        if (_loading) return;
        RefreshValueLabels();
        AudioDirector.Instance?.PreviewSettings(
            (float)(_music.Value / 100.0),
            (float)(_sfx.Value / 100.0),
            (float)(_ambience.Value / 100.0),
            _mute.ButtonPressed,
            _reduced.ButtonPressed,
            _haptics.ButtonPressed);
    }

    private void OnSliderCommitted()
    {
        if (_loading) return;
        EventBus.Cue("ui.toggle");
        Persist();
    }

    private void OnToggleChanged()
    {
        if (_loading) return;
        EventBus.Cue("ui.toggle");
        Persist();
    }

    private void Persist()
    {
        AudioDirector.Instance?.ApplySettings(
            (float)(_music.Value / 100.0),
            (float)(_sfx.Value / 100.0),
            (float)(_ambience.Value / 100.0),
            _mute.ButtonPressed,
            _reduced.ButtonPressed,
            _haptics.ButtonPressed);
    }

    private void RefreshValueLabels()
    {
        _musicValue.Text = $"{(int)Math.Round(_music.Value)}%";
        _sfxValue.Text = $"{(int)Math.Round(_sfx.Value)}%";
        _ambienceValue.Text = $"{(int)Math.Round(_ambience.Value)}%";
    }

    private static HSlider AddSlider(VBoxContainer parent, string label, out Label valueLabel)
    {
        var row = Ui.MakeRow(10f);
        parent.AddChild(row);

        Label name = Ui.MakeLabel(label, 20f, Tok.Text, HorizontalAlignment.Left);
        name.CustomMinimumSize = new Vector2(Tok.Dp(78f), 0);
        row.AddChild(name);

        var slider = new HSlider
        {
            MinValue = 0,
            MaxValue = 100,
            Step = 1,
            Value = 100,
            CustomMinimumSize = new Vector2(0, Tok.Dp(36f)),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        row.AddChild(slider);

        valueLabel = Ui.MakeLabel("100%", 20f, Tok.Muted, HorizontalAlignment.Right);
        valueLabel.CustomMinimumSize = new Vector2(Tok.Dp(40f), 0);
        row.AddChild(valueLabel);
        return slider;
    }

    private static CheckButton AddToggle(VBoxContainer parent, string label)
    {
        var row = Ui.MakeRow(10f);
        parent.AddChild(row);

        var button = new CheckButton
        {
            Text = label,
            ToggleMode = true,
            FocusMode = Control.FocusModeEnum.None,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        button.AddThemeFontSizeOverride("font_size", Tok.I(20f));
        button.AddThemeColorOverride("font_color", Tok.Text);
        button.AddThemeColorOverride("font_pressed_color", Tok.Gold);
        row.AddChild(button);
        return button;
    }
}
