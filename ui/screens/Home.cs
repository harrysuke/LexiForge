using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Home / run entry. No consent wall before gameplay (GDD 3.5): the player can
/// press Play immediately, and ad privacy is a quiet secondary control.
///
/// The screen is cached by SceneRouter, so all UI is built once in _Ready and
/// OnEnter only refreshes text — no per-transition allocation.
/// </summary>
public sealed partial class Home : Control, IScreen
{
    private Label _wallet = null!;
    private Label _stats = null!;
    private Label _status = null!;
    private Label _version = null!;
    private Button _daily = null!;
    private Button _privacy = null!;
    private PauseOverlay _pause = null!;
    private bool _listening;

    public override void _Ready()
    {
        var background = new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 26, 64);

        column.AddChild(Ui.MakeLabel("LEXIFORGE", 76, Ui.Gold));
        column.AddChild(Ui.MakeLabel("Word forging · Heat · Runes", 28, Ui.Muted));

        _wallet = Ui.MakeLabel("", 34, Ui.Text);
        column.AddChild(_wallet);
        _stats = Ui.MakeLabel("", 27, Ui.Muted);
        column.AddChild(_stats);

        var spacer = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(spacer);

        // tapCue is null: StartRun emits run.start (or ui.disabled) itself, so a
        // failed start is silent-ish rather than a cheerful "begin" over an error.
        column.AddChild(Ui.MakeButton("PLAY", () => StartRun(false), Ui.Ember, 132, 42, null));
        _daily = Ui.MakeButton("Daily Forge", () => StartRun(true), Ui.PanelRaised, 112, 32, null);
        column.AddChild(_daily);

        _status = Ui.MakeLabel("", 26, Ui.Ember);
        column.AddChild(_status);
        _version = Ui.MakeLabel("", 20, Ui.Muted);
        column.AddChild(_version);

        _privacy = Ui.MakeButton("Ad privacy: not set", TogglePrivacy, Ui.PanelRaised, 84, 24, "ui.toggle");
        column.AddChild(_privacy);

        _pause = PauseOverlay.AttachTo(this, "Quit game", () => GetTree().Quit());
    }

    public void OnEnter(object? payload)
    {
        StartListening();
        _status.Text = "";
        Refresh();
        // Returning from a run: ad state must no longer be flagged as "in run".
        AdManager.Instance.SetInRun(false);
        AdManager.Instance.SetDailyForge(false);
        AdManager.Instance.SyncFromSave();
    }

    public void OnExit()
    {
        StopListening();
        _pause.Close();
    }

    /// <summary>
    /// Wallet listeners follow the ROUTER's lifecycle, not the node's.
    /// SceneRouter pools screens (remove + re-add), so a _Ready/_ExitTree pair
    /// silently loses the listeners after the first visit. Using _EnterTree
    /// instead is also wrong: autoloads publish their static Instance in _Ready,
    /// which fires AFTER a main scene's _EnterTree — that throws a real
    /// NullReferenceException when a scene is run standalone (F6 / --scene).
    /// OnEnter/OnExit are invoked by SceneRouter only once every autoload is
    /// ready. Idempotent because GoTo() can call OnEnter twice in a row.
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

    /// <summary>Android back opens the pause overlay; it never exits abruptly.</summary>
    public void OnBack() => _pause.Toggle();

    private void OnBalanceChanged(int delta, int balance, string source) => Refresh();

    private void Refresh()
    {
        // Defensive: a wallet signal must never touch Controls that _Ready has
        // not constructed yet, even if a future caller moves StartListening().
        if (_wallet is null) return;
        SaveData save = SaveService.Instance.Data;
        _wallet.Text = $"{EconomyService.Instance.Coins} coins   ·   {EconomyService.Instance.Marks} Marks";
        _stats.Text = $"Forges {save.LifetimeForges}   ·   Stars {save.TotalStars}   ·   " +
                      $"Streak {save.CurrentStreak}   ·   Best forge {save.HighestForgeUnlocked}";
        _version.Text = $"build {ConfigService.Instance.ConfigVersion}   ·   " +
                        $"catalogue {GameDataService.Instance.Forges.Count} forges";
        _daily.Disabled = !ProgressionService.Instance.IsDailyForgeAvailable(GameServices.UtcDay);
        _daily.Text = _daily.Disabled ? "Daily Forge — done today" : "Daily Forge";
        _privacy.Text = save.ConsentResolved
            ? (save.ConsentStatus == "non_personalized" ? "Ad privacy: non-personalized" : "Ad privacy: personalized")
            : "Ad privacy: not set";
    }

    private void StartRun(bool daily)
    {
        var forges = GameDataService.Instance.Forges;
        if (forges.Count == 0)
        {
            EventBus.Cue("ui.disabled");
            _status.Text = "No forge catalogue loaded — restart the app.";
            return;
        }

        SaveData save = SaveService.Instance.Data;
        if (daily && !ProgressionService.Instance.IsDailyForgeAvailable(GameServices.UtcDay))
        {
            EventBus.Cue("ui.disabled");
            _status.Text = "Daily Forge already claimed today.";
            return;
        }

        int seed = daily
            ? ProgressionService.Instance.DailyForgeSeed()
            : unchecked((int)Time.GetUnixTimeFromSystem() ^ (save.LifetimeForges * 7919));
        bool firstRunEver = save.LifetimeForges == 0;

        if (daily)
        {
            save.LastDailyForgeDay = GameServices.UtcDay;
            SaveService.Instance.MarkDirty();
        }

        AdManager.Instance.SetDailyForge(daily);
        RunService.Instance.StartRun(seed, firstRunEver);

        ForgeData? forge = ProgressionService.Instance.ForgeFor(0);
        if (forge is null)
        {
            EventBus.Cue("ui.disabled");
            _status.Text = "No forge available for this run.";
            return;
        }
        EventBus.Cue("run.start");
        SceneRouter.Instance.GoToForge(forge, 0, RunService.Instance.Effects);
    }

    /// <summary>
    /// P0 records the player's choice. Wiring the UMP SDK form + the AdMob app id
    /// is a P1 task (AndroidAdService.Initialize); gameplay never blocks on it.
    /// </summary>
    private void TogglePrivacy()
    {
        SaveData save = SaveService.Instance.Data;
        bool nonPersonalized = save.ConsentStatus != "non_personalized";
        SaveService.Instance.SetConsentResolved(nonPersonalized, save.ConsentPolicyVersion);
        AnalyticsService.Instance.Record("consent", nonPersonalized ? "non_personalized" : "personalized");
        Refresh();
    }

    // Defensive only: SceneRouter calls OnExit before RemoveChild. Guarded by
    // _listening so this is safe during engine shutdown too.
    public override void _ExitTree() => StopListening();
}
