using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// In-game pause. Android's hardware back is routed here by SceneRouter's
/// NOTIFICATION_WM_GO_BACK_REQUEST; quit_on_go_back is false, so the back button
/// can never kill the app mid-forge (GDD 11.5).
///
/// ProcessMode is Always because GetTree().Paused is set to true while the
/// overlay is open — without it, the Resume button itself would stop receiving
/// input. It is a child of the screen, so it is pooled with the scene.
/// </summary>
public sealed partial class PauseOverlay : Control
{
    private readonly string _secondaryLabel;
    private readonly Action _onSecondary;
    private AudioSettingsPanel _settings = null!;

    private PauseOverlay(string secondaryLabel, Action onSecondary)
    {
        _secondaryLabel = secondaryLabel;
        _onSecondary = onSecondary;
        ProcessMode = ProcessModeEnum.Always;
        Visible = false;
    }

    /// <summary>Creates and attaches one overlay per hosting screen.</summary>
    public static PauseOverlay AttachTo(Control host, string secondaryLabel, Action onSecondary)
    {
        var overlay = new PauseOverlay(secondaryLabel, onSecondary);
        host.AddChild(overlay);
        return overlay;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Ui.FullRect(this);
        // Set above every other child of the host, including the results card.
        MoveToFront();

        var dim = new ColorRect { Color = new Color(0.02f, 0.015f, 0.01f, 0.86f) };
        AddChild(dim);
        Ui.FullRect(dim);

        var centre = new CenterContainer();
        AddChild(centre);
        Ui.FullRect(centre);

        var card = Ui.MakePanel(Ui.Panel, 28);
        card.CustomMinimumSize = new Vector2(760, 0);
        centre.AddChild(card);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 20);
        card.AddChild(column);

        column.AddChild(Ui.MakeLabel("PAUSED", 48, Ui.Gold));
        column.AddChild(Ui.MakeLabel("Your forge is safe. Take your time.", 26, Ui.Muted));
        // tapCue is null on every button here: Open()/Close() and the settings
        // panel emit their own cues, so a tap never doubles up (section 4.2).
        column.AddChild(Ui.MakeButton("Resume", Close, Ui.Ember, 110, 34, null));
        column.AddChild(Ui.MakeButton("Sound", () => _settings.Open(), Ui.PanelRaised, 96, 28, null));
        column.AddChild(Ui.MakeButton(_secondaryLabel, () =>
        {
            Close();
            _onSecondary();
        }, Ui.PanelRaised, 100, 28, null));

        // Added last so it draws above the pause card.
        _settings = AudioSettingsPanel.AttachTo(this);
    }

    public bool IsOpen => Visible;

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
        EventBus.Cue("ui.open");
        SceneRouter.Instance.SetPaused(true);
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
}
