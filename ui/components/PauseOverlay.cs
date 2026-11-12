using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// In-game pause. Its job is to BE BORING (UI-58).
///
///  - A sheet from the bottom, never a full-screen takeover: the grid stays
///    visible above it, because leaving a puzzle should never feel like being
///    ejected from it.
///  - "Leave run" states its consequence once, in Rust text, with no confirmation
///    modal stacked on a confirmation.
///  - The two accessibility toggles (reduced motion, haptics) live here because
///    they are settings, not gameplay.
///
/// ProcessMode is Always because GetTree().Paused is true while the overlay is
/// open - without it, the Resume button itself would stop receiving input.
/// </summary>
public sealed partial class PauseOverlay : Control
{
    private readonly string _secondaryLabel;
    private readonly Action _onSecondary;
    private readonly Action? _onHome;

    private ColorRect _dim = null!;
    private PanelContainer _sheet = null!;
    private Button _secondary = null!;
    private Button _animToggle = null!;
    private Button _hapticToggle = null!;
    private AudioSettingsPanel _settings = null!;

    private PauseOverlay(string secondaryLabel, Action onSecondary, Action? onHome)
    {
        _secondaryLabel = secondaryLabel;
        _onSecondary = onSecondary;
        _onHome = onHome;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    /// <summary>Creates and attaches one overlay per hosting screen.</summary>
    public static PauseOverlay AttachTo(Control host, string secondaryLabel, Action onSecondary, Action? onHome = null)
    {
        var overlay = new PauseOverlay(secondaryLabel, onSecondary, onHome);
        host.AddChild(overlay);
        return overlay;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Ui.FullRect(this);
        MoveToFront();

        _dim = new ColorRect { Color = Tok.A(Tok.BgVoid, 0.40f) };
        AddChild(_dim);
        Ui.FullRect(_dim);

        _sheet = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Tok.Surf1 };
        style.CornerRadiusTopLeft = Tok.I(24f);
        style.CornerRadiusTopRight = Tok.I(24f);
        style.ContentMarginLeft = Tok.I(Tok.MarginDp);
        style.ContentMarginRight = Tok.I(Tok.MarginDp);
        style.ContentMarginTop = Tok.I(18f);
        style.ContentMarginBottom = Tok.I(18f);
        _sheet.AddThemeStyleboxOverride("panel", style);
        AddChild(_sheet);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(8f));
        _sheet.AddChild(column);

        // tapCue is null on every row: Open()/Close() and the settings panel emit
        // their own cues, so a tap never doubles up (AUDIO_DESIGN section 4.2).
        column.AddChild(Ui.MakeButton("Resume", Close, BtnKind.Primary, 56f, 22f, null));
        column.AddChild(Ui.MakeButton("Audio settings", () => _settings.Open(), BtnKind.Secondary, 56f, 22f, null));

        _animToggle = Ui.MakeButton("", ToggleAnimations, BtnKind.Secondary, 56f, 22f, null);
        column.AddChild(_animToggle);
        _hapticToggle = Ui.MakeButton("", ToggleHaptics, BtnKind.Secondary, 56f, 22f, null);
        column.AddChild(_hapticToggle);

        // The consequence is stated once, inside the same target (UI-58). Callers
        // pass "Leave run — this forge starts over"; splitting on the dash keeps the
        // sheet inside the 360 dp column, where one long string made it 106 dp wider
        // than the screen on both sides.
        string title = _secondaryLabel;
        string subtitle = "";
        int dash = _secondaryLabel.IndexOf('—');
        if (dash > 0)
        {
            title = _secondaryLabel[..dash].Trim();
            subtitle = _secondaryLabel[(dash + 1)..].Trim();
        }

        _secondary = Ui.MakeCta(title, subtitle, () =>
        {
            Close();
            _onSecondary();
        }, subtitle.Length > 0 ? 84f : 56f, null, BtnKind.DangerNeutral, Tok.RustText);
        column.AddChild(_secondary);

        if (_onHome is not null)
            column.AddChild(Ui.MakeButton("Home", () =>
            {
                Close();
                _onHome();
            }, BtnKind.Ghost, 44f, 20f, "ui.back"));

        // Added last so it draws above the sheet.
        _settings = AudioSettingsPanel.AttachTo(this);
        Resized += LayoutSheet;
    }

    public bool IsOpen => Visible;

    private void LayoutSheet()
    {
        if (_sheet is null) return;
        float width = Mathf.Min(Size.X, Tok.Dp(Tok.DesignWidthDp));
        float content = _sheet.GetCombinedMinimumSize().Y;
        float height = Mathf.Min(content, Size.Y * 0.82f);
        _sheet.Size = new Vector2(width, height);
        _sheet.Position = new Vector2((Size.X - width) * 0.5f, Size.Y - height);
    }

    public void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    public void Open()
    {
        if (IsOpen) return;
        Visible = true;
        MoveToFront();
        RefreshToggles();
        LayoutSheet();
        EventBus.Cue("ui.open");
        SceneRouter.Instance.SetPaused(true);

        // 220 ms sheet rise; nothing else animates here.
        Vector2 target = _sheet.Position;
        _sheet.Position = new Vector2(target.X, Size.Y);
        UiAnim.On(_sheet, "sheet")
              .TweenProperty(_sheet, "position", target, UiAnim.Dur(Tok.TSheet))
              .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
    }

    public void Close()
    {
        if (!IsOpen) return;
        Visible = false;
        // Settings live above the pause card, so closing the overlay must take
        // them with it; otherwise a pooled screen would keep a stale panel.
        _settings?.Close();
        EventBus.Cue("ui.close");
        SceneRouter.Instance.SetPaused(false);
    }

    private void RefreshToggles()
    {
        SaveData save = SaveService.Instance.Data;
        _animToggle.Text = $"Remove animations — {(save.RemoveAnimations ? "On" : "Off")}";
        _hapticToggle.Text = $"Haptics — {(save.HapticsEnabled ? "On" : "Off")}";
    }

    private void ToggleAnimations()
    {
        SaveData save = SaveService.Instance.Data;
        save.RemoveAnimations = !save.RemoveAnimations;
        SaveService.Instance.MarkDirty();
        EventBus.Cue("ui.toggle");
        RefreshToggles();
    }

    private void ToggleHaptics()
    {
        SaveData save = SaveService.Instance.Data;
        save.HapticsEnabled = !save.HapticsEnabled;
        SaveService.Instance.MarkDirty();
        EventBus.Cue("ui.toggle");
        UiAnim.Haptic(14);
        RefreshToggles();
    }
}
