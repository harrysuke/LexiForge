using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// Main scene. Owns the screen host and the ONE decision Boot is allowed to
/// make: has the dictionary + forge catalogue finished loading?
///
/// GameDataService._Ready only registers itself; StartLoad() is called here so
/// the DataReady subscription is guaranteed to exist before the worker thread
/// marshals its result back with CallDeferred. A corrupt PCK produces an
/// explicit, retryable error instead of an endless splash.
/// </summary>
public sealed partial class Boot : Control
{
    private Control _splash = null!;
    private Label _status = null!;
    private Button _retry = null!;
    private bool _navigated;

    public override void _Ready()
    {
        var background = new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var frame = new MarginContainer();
        frame.AddThemeConstantOverride("margin_left", 80);
        frame.AddThemeConstantOverride("margin_right", 80);
        frame.AddThemeConstantOverride("margin_top", 160);
        frame.AddThemeConstantOverride("margin_bottom", 160);
        AddChild(frame);
        Ui.FullRect(frame);
        _splash = frame;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 36);
        frame.AddChild(column);

        column.AddChild(Ui.MakeLabel("LEXIFORGE", 86, Ui.Gold));
        column.AddChild(Ui.MakeLabel("Heat the iron. Keep the Flow.", 30, Ui.Muted));
        _status = Ui.MakeLabel("Loading word forge...", 30, Ui.Text);
        column.AddChild(_status);

        _retry = Ui.MakeButton("Retry load", OnRetry, Ui.Ember, 104, 30);
        _retry.Visible = false;
        column.AddChild(_retry);

        EventBus.Instance.DataReady += OnDataReady;
        SceneRouter.Instance.AttachHost(this);
        GameDataService.Instance.StartLoad();
        // The parse can complete before Boot._Ready only in the synchronous
        // failure path (Fail uses CallDeferred), so this check is a safety net.
        if (GameDataService.Instance.IsReady) OnDataReady();
    }

    private void OnDataReady()
    {
        if (_navigated) return;

        if (!GameDataService.Instance.IsReady)
        {
            _status.Text = "Could not load game data:\n" + GameDataService.Instance.ErrorMessage;
            _status.AddThemeColorOverride("font_color", Ui.Bad);
            _retry.Visible = true;
            return;
        }

        _navigated = true;
        _splash.Visible = false;
        SceneRouter.Instance.GoHome();
        AdManager.Instance.TryShowAppOpen();
    }

    private void OnRetry()
    {
        _retry.Visible = false;
        _status.Text = "Loading word forge...";
        _status.AddThemeColorOverride("font_color", Ui.Text);
        GameDataService.Instance.StartLoad();
    }

    public override void _ExitTree() => EventBus.Instance.DataReady -= OnDataReady;
}
