using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Forge-level HUD: Flow, Bonus Jar, slot progress, run Heat, wallet and the
/// hint / pause affordances. It never mutates state — ForgeScreen owns the
/// ForgeSession and calls Refresh()/Toast(). Signals keep the HUD reusable in
/// the editor without any knowledge of the screen that hosts it.
///
/// The toast is a single pooled Label whose colour/text are rewritten, never a
/// spawned node per word (GDD 11.5).
/// </summary>
public sealed partial class ForgeHud : Control
{
    [Signal] public delegate void HintRequestedEventHandler();
    [Signal] public delegate void RewardedHintRequestedEventHandler();
    [Signal] public delegate void PauseRequestedEventHandler();

    private Label _flow = null!;
    private Label _slots = null!;
    private Label _bonus = null!;
    private Label _wallet = null!;
    private Label _heat = null!;
    private Label _toast = null!;
    private ProgressBar _bar = null!;
    private Button _hint = null!;
    private Button _adHint = null!;
    private double _toastRemaining;

    public override void _Ready()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 10);
        AddChild(column);
        Ui.FullRect(column);

        var top = Ui.MakeRow(12);
        column.AddChild(top);

        _flow = Ui.MakeLabel("FLOW 1.00x", 30, Ui.Gold, HorizontalAlignment.Left);
        _flow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        top.AddChild(_flow);

        _wallet = Ui.MakeLabel("0", 28, Ui.Text, HorizontalAlignment.Right);
        top.AddChild(_wallet);

        var meters = Ui.MakeRow(14);
        column.AddChild(meters);

        _heat = Ui.MakeLabel("HEAT 0", 24, Ui.Muted, HorizontalAlignment.Left);
        _heat.CustomMinimumSize = new Vector2(190, 0);
        meters.AddChild(_heat);

        _bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 100,
            Value = 0,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(0, 26),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        _bar.AddThemeStyleboxOverride("background", Flat(Ui.Locked));
        _bar.AddThemeStyleboxOverride("fill", Flat(Ui.Ember));
        meters.AddChild(_bar);

        var stats = Ui.MakeRow(14);
        column.AddChild(stats);
        _slots = Ui.MakeLabel("0 / 0 words", 26, Ui.Muted, HorizontalAlignment.Left);
        _slots.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        stats.AddChild(_slots);
        _bonus = Ui.MakeLabel("BONUS 0 / 8", 26, Ui.Muted, HorizontalAlignment.Right);
        stats.AddChild(_bonus);

        var actions = Ui.MakeRow(12);
        column.AddChild(actions);
        // These emit their own semantic cues downstream: hint.reveal on
        // HintGranted, ui.disabled on a rejected hint, ui.open when the pause
        // overlay opens. A generic tap here would double every one of them.
        _hint = Ui.MakeButton("Hint", () => EmitSignal(SignalName.HintRequested), Ui.PanelRaised, 92, 26, null);
        actions.AddChild(_hint);
        _adHint = Ui.MakeButton("Ad: free hint", () => EmitSignal(SignalName.RewardedHintRequested),
                                Ui.PanelRaised, 92, 24, null);
        _adHint.Visible = false;
        actions.AddChild(_adHint);
        var pause = Ui.MakeButton("II", () => EmitSignal(SignalName.PauseRequested), Ui.PanelRaised, 92, 28, null);
        pause.CustomMinimumSize = new Vector2(110, 92);
        pause.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        actions.AddChild(pause);

        _toast = Ui.MakeLabel("", 30, Ui.Text);
        _toast.CustomMinimumSize = new Vector2(0, 56);
        _toast.Modulate = new Color(1, 1, 1, 0);
        column.AddChild(_toast);
    }

    private static StyleBoxFlat Flat(Color color)
    {
        var box = new StyleBoxFlat { BgColor = color };
        box.SetCornerRadiusAll(10);
        return box;
    }

    public void ResetFor(ForgeSession session) => Refresh(session);

    public void Refresh(ForgeSession session)
    {
        var cfg = ConfigService.Instance.Config;
        _flow.Text = $"FLOW {session.Flow:0.00}x   {session.ElapsedSeconds:0}s";
        _flow.AddThemeColorOverride("font_color", session.Flow > 1.001 ? Ui.Gold : Ui.Text);

        double heat = RunService.Instance.Heat;
        _heat.Text = $"HEAT {heat:0}";
        _bar.Value = heat;

        _slots.Text = $"{session.FoundSolutions.Count} / {session.SolutionWordCount} words";
        _bonus.Text = $"BONUS {session.BonusJar.Count} / {cfg.Economy.BonusWordCap}";
        _wallet.Text = $"{EconomyService.Instance.Coins} coins";

        int cost = EconomyModel.HintCost(session.HintsUsed + 1, cfg.Economy);
        bool exhausted = session.HintsUsed >= cfg.Economy.MaxHintsPerForge;
        _hint.Text = session.FreeHintsRemaining > 0 ? "Free hint" : $"Hint ({cost})";
        _hint.Disabled = exhausted || (session.FreeHintsRemaining == 0 && EconomyService.Instance.Coins < cost);

        // Rewarded entry points are visible but never pushed as a popup (GDD 3.5).
        bool firstSessionUnlocked = SaveService.Instance.Data.LifetimeForges + 1
                                    >= cfg.Progression.FirstSession.RewardedEntryPointsVisibleFromForge;
        _adHint.Visible = firstSessionUnlocked && !exhausted &&
                          AdManager.Instance.CanOfferRewarded("R_HINT", out _);
    }

    public void Toast(string text, Color color)
    {
        _toast.Text = text;
        _toast.AddThemeColorOverride("font_color", color);
        _toast.Modulate = Colors.White;
        _toastRemaining = 1.6;
    }

    public override void _Process(double delta)
    {
        if (_toastRemaining <= 0) return;
        _toastRemaining -= delta;
        if (_toastRemaining > 0)
        {
            // Hold full opacity, then fade over the last 0.5 s.
            float alpha = _toastRemaining < 0.5 ? (float)(_toastRemaining / 0.5) : 1f;
            _toast.Modulate = new Color(1, 1, 1, alpha);
        }
        else
        {
            _toast.Modulate = new Color(1, 1, 1, 0);
        }
    }
}
