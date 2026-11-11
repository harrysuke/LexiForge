using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// A run ending is a REWARD moment, never a loss screen (GDD 3.3). The player
/// may always start a fresh run for free; "Cool the forge" is an optional
/// continuation with three equivalent prices — 1 rewarded ad, coins, or 1 Mark —
/// so two of the three choices are free-to-play currencies and none is a paywall.
/// </summary>
public sealed partial class RunEndScreen : Control, IScreen
{
    private Label _score = null!;
    private Label _details = null!;
    private Label _runes = null!;
    private Label _status = null!;
    private Button _adCool = null!;
    private Button _coinCool = null!;
    private Button _markCool = null!;
    private PauseOverlay _pause = null!;
    private RunEndInfo? _info;
    private int _runIndex;
    private bool _adBusy;
    private bool _cooled;
    private bool _listening;

    public override void _Ready()
    {
        var background = new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 24, 56);
        column.AddChild(Ui.MakeLabel("RUN COMPLETE", 62, Ui.Gold));
        _score = Ui.MakeLabel("", 56, Ui.Ember);
        column.AddChild(_score);
        _details = Ui.MakeLabel("", 28, Ui.Text);
        column.AddChild(_details);
        _runes = Ui.MakeLabel("", 26, Ui.Muted);
        column.AddChild(_runes);

        var coolPanel = Ui.MakePanel(Ui.Panel, 24);
        column.AddChild(coolPanel);
        var coolColumn = new VBoxContainer();
        coolColumn.AddThemeConstantOverride("separation", 14);
        coolPanel.AddChild(coolColumn);
        coolColumn.AddChild(Ui.MakeLabel("Cool the forge  ·  −25 Heat  ·  keep this run going", 26, Ui.Gold));

        _adCool = Ui.MakeButton("", OnAdCool, Ui.PanelRaised, 96, 26);
        coolColumn.AddChild(_adCool);
        _coinCool = Ui.MakeButton("", OnCoinCool, Ui.PanelRaised, 96, 26);
        coolColumn.AddChild(_coinCool);
        _markCool = Ui.MakeButton("", OnMarkCool, Ui.PanelRaised, 96, 26);
        coolColumn.AddChild(_markCool);

        _status = Ui.MakeLabel("", 26, Ui.Good);
        column.AddChild(_status);

        column.AddChild(Ui.MakeButton("NEW RUN", OnNewRun, Ui.Ember, 120, 38));
        column.AddChild(Ui.MakeButton("Home", () => SceneRouter.Instance.GoHome(), Ui.PanelRaised, 96, 28));

        _pause = PauseOverlay.AttachTo(this, "Quit game", () => GetTree().Quit());
    }

    public void OnEnter(object? payload)
    {
        StartListening();
        _info = payload is RunEndPayload p ? p.Info : null;
        if (payload is RunEndPayload typed) _runIndex = typed.RunIndex;
        _cooled = false;
        _status.Text = "";
        _pause.Close();
        Render();

        // SetInRun(false) already happened inside RunService.ApplyForge, so the
        // policy can legally evaluate an interstitial at this natural break.
        AdManager.Instance.ShowInterstitialIfAllowed("I_RUN_END");
    }

    public void OnExit()
    {
        StopListening();
        _pause.Close();
    }
    public void OnBack() => _pause.Toggle();

    /// <summary>
    /// Same router-owned lifecycle as Home: _Ready/_ExitTree loses listeners on
    /// pooled screens, while _EnterTree runs before autoloads publish their
    /// static Instance (NullReferenceException under F6 / --scene). Idempotent
    /// because GoTo() may call OnEnter twice without an intervening OnExit.
    /// </summary>
    private void StartListening()
    {
        if (_listening) return;
        EventBus.Instance.CoinsChanged += OnBalanceChanged;
        EventBus.Instance.MarksChanged += OnBalanceChanged;
        _listening = true;
    }

    private void StopListening()
    {
        if (!_listening) return;
        EventBus.Instance.CoinsChanged -= OnBalanceChanged;
        EventBus.Instance.MarksChanged -= OnBalanceChanged;
        _listening = false;
    }

    private void OnBalanceChanged(int delta, int balance, string source) => Render();

    private void Render()
    {
        // Defensive: never touch Controls before _Ready has built them, even if
        // a future caller moves StartListening() earlier in the lifecycle.
        if (_score is null) return;
        RunEndInfo info = _info ?? new RunEndInfo
        {
            TotalScore = RunService.Instance.RunScore,
            Forges = RunService.Instance.ForgesInRun,
            FinalHeat = RunService.Instance.Heat,
            FreeMarks = 0,
            Runes = Array.Empty<string>()
        };

        int score = Math.Max(info.TotalScore, RunService.Instance.RunScore);
        double heat = RunService.Instance.Heat > 0 ? RunService.Instance.Heat : info.FinalHeat;
        _score.Text = $"{score} points";
        _details.Text = $"{info.Forges} forges  ·  final Heat {heat:0}/100\n" +
                        $"Wallet: {EconomyService.Instance.Coins} coins  ·  {EconomyService.Instance.Marks} Marks";
        _runes.Text = info.Runes.Count == 0 ? "Runes drafted: —" : "Runes drafted: " + string.Join(", ", info.Runes);

        var cfg = ConfigService.Instance.Config;
        _adCool.Text = "Watch a rewarded ad (opt-in)";
        _coinCool.Text = $"Pay {cfg.Sinks.CoolForgeCoins} coins";
        _markCool.Text = "Spend 1 Sponsor Mark";

        bool available = !_cooled;
        _adCool.Visible = available;
        _coinCool.Visible = available;
        _markCool.Visible = available;
        _adCool.Disabled = _adBusy || !AdManager.Instance.CanOfferRewarded("R_COOL", out _);
        _coinCool.Disabled = EconomyService.Instance.Coins < cfg.Sinks.CoolForgeCoins;
        _markCool.Disabled = EconomyService.Instance.Marks < 1;
    }

    private async void OnAdCool()
    {
        if (_cooled || _adBusy) return;
        _adBusy = true;
        try
        {
            bool granted = await AdManager.Instance.TryRewardedAsync("R_COOL", 0);
            if (granted) Cool();
            else _status.Text = "Ad unavailable — the coin and Mark options are still free to use.";
        }
        finally
        {
            _adBusy = false;
            Render();
        }
    }

    private void OnCoinCool()
    {
        if (_cooled) return;
        int cost = ConfigService.Instance.Config.Sinks.CoolForgeCoins;
        if (EconomyService.Instance.TrySpendCoins(cost, "cool_forge")) Cool();
        else _status.Text = "Not enough coins.";
    }

    private void OnMarkCool()
    {
        if (_cooled) return;
        if (EconomyService.Instance.TrySpendMarks(1, "cool_forge")) Cool();
        else _status.Text = "No Sponsor Marks left.";
    }

    private void Cool()
    {
        _cooled = true;
        RunService.Instance.CoolForge();
        AnalyticsService.Instance.Record("cool_forge", ((int)Math.Round(RunService.Instance.Heat)).ToString());
        Render();
        NextForge();
    }

    private void OnNewRun()
    {
        SaveData save = SaveService.Instance.Data;
        int seed = unchecked((int)Time.GetUnixTimeFromSystem() ^ (save.LifetimeForges * 7919));
        AdManager.Instance.SetDailyForge(false);
        RunService.Instance.StartRun(seed, false);
        ForgeData? forge = ProgressionService.Instance.ForgeFor(0);
        if (forge is null) { SceneRouter.Instance.GoHome(); return; }
        SceneRouter.Instance.GoToForge(forge, 0, RunService.Instance.Effects);
    }

    /// <summary>
    /// Cooling extends the run rather than ending it again: RunState raises its
    /// forge cap when CoolForge() is called, so the next ApplyForge continues.
    /// </summary>
    private void NextForge()
    {
        int next = _runIndex + 1;
        ForgeData? forge = ProgressionService.Instance.ForgeFor(next);
        if (forge is null) { SceneRouter.Instance.GoHome(); return; }
        AdManager.Instance.SetInRun(true);
        SceneRouter.Instance.GoToForge(forge, next, RunService.Instance.Effects);
    }

    // SceneRouter calls OnExit before RemoveChild; this is a shutdown guard.
    public override void _ExitTree() => StopListening();
}
