using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The Read band: Heat, wallet, Flow, Bonus Jar and slot progress. Read-only by
/// contract (UI_VISUAL_SYSTEM section 5.1) - the only interactive things here are
/// the pause control and the Heat bar's tap-to-peek.
///
/// Attention order this layout encodes (UI_DESIGN section 4.2):
///   empty slots (in the grid) > rack > Flow > Heat > chrome.
/// Everything in this band is therefore quiet by default: Heat is a 10 dp strip
/// with no number, the wallet is low contrast, and the theme line is 20 sp Muted.
///
/// It never mutates state: ForgeScreen owns the session and calls Refresh().
/// </summary>
public sealed partial class ForgeHud : Control
{
    [Signal] public delegate void PauseRequestedEventHandler();

    private HeatBar _heatBar = null!;
    private FlowMeter _flow = null!;
    private BonusJar _jar = null!;
    private Label _theme = null!;
    private Label _slots = null!;
    private Label _wallet = null!;
    private Button _pause = null!;
    private int _lastFound = -1;
    private int _lastBonus = -1;

    public BonusJar Jar => _jar;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        _heatBar = new HeatBar();
        AddChild(_heatBar);
        _heatBar.SetAnchorsPreset(LayoutPreset.TopWide);
        _heatBar.OffsetLeft = 0;
        _heatBar.OffsetRight = 0;
        _heatBar.OffsetTop = 0;
        _heatBar.OffsetBottom = Tok.Dp(10f);

        var frame = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        frame.AddThemeConstantOverride("margin_left", Tok.I(Tok.MarginDp));
        frame.AddThemeConstantOverride("margin_right", Tok.I(Tok.MarginDp));
        frame.AddThemeConstantOverride("margin_top", Tok.I(12f));
        frame.AddThemeConstantOverride("margin_bottom", Tok.I(4f));
        AddChild(frame);
        Ui.FullRect(frame);
        frame.OffsetTop = Tok.Dp(10f);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", Tok.I(6f));
        frame.AddChild(column);

        // Row 1: pause (the quietest thing on screen) · theme · wallet.
        var top = Ui.MakeRow(8f);
        column.AddChild(top);

        _pause = Ui.MakeButton("", () => EmitSignal(SignalName.PauseRequested), BtnKind.Icon, 44f, 20f, null);
        _pause.CustomMinimumSize = new Vector2(Tok.Dp(44f), Tok.Dp(44f));
        _pause.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _pause.Modulate = new Color(1, 1, 1, 0.6f);
        _pause.AddChild(Fill(IconGlyph.Create(Glyph.Pause, 16f, Tok.Muted, 2f)));
        top.AddChild(_pause);

        // UI-07: theme at 20 sp Muted, and DI is gone - it is an internal
        // difficulty index, not a player concept.
        _theme = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left);
        _theme.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _theme.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        top.AddChild(_theme);

        var walletRow = Ui.MakeRow(4f);
        walletRow.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        walletRow.AddChild(IconGlyph.Create(Glyph.Coin, 14f, Tok.Gold, 1.6f));
        _wallet = Ui.MakeLabel("0", 22f, Tok.Text, HorizontalAlignment.Right);
        walletRow.AddChild(_wallet);
        top.AddChild(walletRow);

        // Row 2: Flow (the E1 feedback) and the jar (the E3 carrier).
        var meters = Ui.MakeRow(10f);
        column.AddChild(meters);

        _flow = new FlowMeter { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        meters.AddChild(_flow);

        _jar = new BonusJar { SizeFlagsHorizontal = SizeFlags.ShrinkEnd, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        meters.AddChild(_jar);

        // Row 3: the gap, quantified.
        var progress = Ui.MakeRow(8f);
        column.AddChild(progress);
        _slots = Ui.MakeLabel("", 24f, Tok.Muted, HorizontalAlignment.Left);
        _slots.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        progress.AddChild(_slots);
    }

    private static Control Fill(Control child)
    {
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        centre.AddChild(child);
        return centre;
    }

    public void ResetFor(ForgeSession session)
    {
        _lastFound = -1;
        _lastBonus = -1;
        _heatBar.SetHeat(RunService.Instance.Heat, RunService.Instance.RunScore, false);
        Refresh(session);
    }

    public void Refresh(ForgeSession session)
    {
        var cfg = ConfigService.Instance.Config;
        int found = session.FoundSolutions.Count;
        int total = session.SolutionWordCount;
        int remaining = Mathf.Max(0, total - found);

        _theme.Text = session.Forge.Theme;
        _wallet.Text = EconomyService.Instance.Coins.ToString("N0");
        _flow.SetFlow(session.Flow, _lastFound >= 0);
        _jar.SetFill(session.BonusJar.Count, cfg.Economy.BonusWordCap, _lastBonus >= 0);

        // UI-29: goal-gradient. The counter turns Gold only at <=2 remaining, and
        // only then gains the suffix - doing this at 5 remaining is nagging.
        if (remaining <= 2 && remaining > 0)
        {
            _slots.Text = $"{found} / {total} words  —  {remaining} word{(remaining == 1 ? "" : "s")} left";
            _slots.AddThemeColorOverride("font_color", Tok.Gold);
        }
        else
        {
            _slots.Text = $"{found} / {total} words";
            _slots.AddThemeColorOverride("font_color", Tok.Muted);
        }

        // UI-61: no HUD element until it has something to say. A tutorial player
        // does not see an empty jar or an empty Flow bar on forge 1.
        bool firstRun = RunService.Instance.IsFirstRunEver;
        _jar.Visible = !firstRun || session.BonusJar.Count > 0;
        _flow.Visible = !firstRun || session.CleanStreak > 0;

        _lastFound = found;
        _lastBonus = session.BonusJar.Count;
    }

    public void SetHeat(double heat, int score, bool animate)
        => _heatBar.SetHeat(heat, score, animate);

    /// <summary>A coin arcs from the cell the player just filled into the jar.</summary>
    public void CoinIntoJar(Vector2 globalFrom) => _jar.FlyCoinFrom(globalFrom);

    public void BurstJar() => _jar.Burst();
}
