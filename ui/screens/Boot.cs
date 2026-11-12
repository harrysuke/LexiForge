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
        Tok.Setup(GetViewportRect().Size.X);

        var background = new ColorRect { Color = Tok.BgBase, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var frame = new MarginContainer();
        frame.AddThemeConstantOverride("margin_left", Tok.I(Tok.MarginDp));
        frame.AddThemeConstantOverride("margin_right", Tok.I(Tok.MarginDp));
        frame.AddThemeConstantOverride("margin_top", Tok.I(64f));
        frame.AddThemeConstantOverride("margin_bottom", Tok.I(64f));
        AddChild(frame);
        Ui.FullRect(frame);
        _splash = frame;

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(12f));
        frame.AddChild(column);

        column.AddChild(Ui.Display(Ui.MakeLabel("LEXIFORGE", 34f, Tok.Text, HorizontalAlignment.Left), this));
        column.AddChild(Ui.MakeLabel("Heat the iron. Keep the Flow.", 22f, Tok.Muted, HorizontalAlignment.Left));
        _status = Ui.MakeLabel("Loading word forge…", 22f, Tok.Text, HorizontalAlignment.Left);
        column.AddChild(_status);

        _retry = Ui.MakeButton("Retry load", OnRetry, BtnKind.Secondary, 56f, 22f);
        _retry.Visible = false;
        _retry.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
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
