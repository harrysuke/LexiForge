using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Home is not a lobby. It is a LAUNCH PAD WITH A MEMORY (UI_DESIGN 5.1), and the
/// genre's churn happens exactly here, so every element either removes a decision
/// or shows stored value.
///
///  - The primary is "Continue run — Forge 3 of 8 · Heat 41", never a generic
///    "PLAY": a resumed run is a specific commitment the player already made, and
///    naming the state removes one decision (UI-21).
///  - With no run in progress it becomes "Light the forge" with the player's own
///    history underneath - the endowment hook (UI-22).
///  - The rune build strip is visible, never behind a menu (UI-23): it is the
///    reason a returning player has a plan.
///  - The streak lives INSIDE the Daily Forge row (UI-24), because a streak
///    attached to the thing it belongs to is information, and a separate banner is
///    a nag.
///  - Wallet and privacy are the quietest elements. No consent wall before
///    gameplay (UI-25).
///
/// The screen is cached by SceneRouter, so all UI is built once in _Ready and
/// OnEnter only refreshes text - no per-transition allocation.
/// </summary>
public sealed partial class Home : Control, IScreen
{
    private Label _wallet = null!;
    private Label _stats = null!;
    private Label _status = null!;
    private Label _version = null!;
    private Label _buildCaption = null!;
    private Button _primary = null!;
    private Button _daily = null!;
    private Button _privacy = null!;
    private Label _dailyLabel = null!;
    private Label _dailyNote = null!;
    private Label _streakLabel = null!;
    private BuildStrip _build = null!;
    private RewardedRow _gift = null!;
    private PauseOverlay _pause = null!;
    private bool _listening;

    public override void _Ready()
    {
        Tok.Setup(GetViewportRect().Size.X);

        var background = new ColorRect { Color = Tok.BgBase, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 12f, Tok.MarginDp);

        // --- wordmark + settings. 34 sp, not 76 sp: a large logo is a splash
        // screen, and a splash screen is a second spent not playing.
        var header = Ui.MakeRow(8f);
        column.AddChild(header);
        Label wordmark = Ui.Display(Ui.MakeLabel("LEXIFORGE", 34f, Tok.Text, HorizontalAlignment.Left), this);
        wordmark.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(wordmark);

        Button settings = Ui.MakeButton("", () => _pause.Toggle(), BtnKind.Icon, 44f, 20f, null);
        settings.CustomMinimumSize = new Vector2(Tok.Dp(44f), Tok.Dp(44f));
        settings.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        settings.AddChild(Fill(IconGlyph.Create(Glyph.Gear, 20f, Tok.Muted, 1.8f)));
        header.AddChild(settings);

        column.AddChild(Ui.FixedSpacer(12f));

        // --- primary CTA. tapCue null: StartRun emits run.start / ui.disabled
        // itself, so a failed start is never cheerful.
        _primary = Ui.MakeCta("LIGHT THE FORGE", "", () => StartRun(false), 120f, null);
        column.AddChild(_primary);

        // --- Daily Forge with the streak inside the row. Two lines, always: a
        // 88 dp row holding one short label reads as an unfinished element, and a
        // blank right-hand side is where the streak was supposed to be.
        _daily = Ui.MakeButton("", () => StartRun(true), BtnKind.Secondary, 76f, 22f, null);
        column.AddChild(_daily);
        var dailyRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        dailyRow.AddThemeConstantOverride("separation", Tok.I(8f));
        dailyRow.SetAnchorsPreset(LayoutPreset.FullRect);
        dailyRow.OffsetLeft = Tok.Dp(16f);
        dailyRow.OffsetRight = -Tok.Dp(16f);
        _daily.AddChild(dailyRow);

        var dailyLines = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        dailyLines.AddThemeConstantOverride("separation", Tok.I(1f));
        dailyRow.AddChild(dailyLines);

        _dailyLabel = Ui.MakeLabel("Daily Forge", 22f, Tok.Text, HorizontalAlignment.Left);
        _dailyLabel.MouseFilter = MouseFilterEnum.Ignore;
        dailyLines.AddChild(_dailyLabel);

        _dailyNote = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left);
        _dailyNote.MouseFilter = MouseFilterEnum.Ignore;
        dailyLines.AddChild(_dailyNote);

        _streakLabel = Ui.MakeLabel("", 20f, Tok.Gold, HorizontalAlignment.Right);
        _streakLabel.MouseFilter = MouseFilterEnum.Ignore;
        _streakLabel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        dailyRow.AddChild(_streakLabel);

        // --- rune build strip (E4).
        _buildCaption = Ui.MakeLabel("RUNE BUILD", 20f, Tok.Muted, HorizontalAlignment.Left);
        column.AddChild(_buildCaption);
        _build = new BuildStrip();
        column.AddChild(_build);

        // --- collection progress, quiet. Two numbers, not four: four numbers at
        // equal weight is a wall, not a hierarchy (UI-26).
        _stats = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left);
        column.AddChild(_stats);

        column.AddChild(Ui.Spacer());

        // --- wallet, low contrast by design: it is not a thing to do.
        var divider = new ColorRect { Color = Tok.LineHair, CustomMinimumSize = new Vector2(0, Tok.Dp(1f)) };
        divider.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(divider);

        var walletRow = Ui.MakeRow(8f);
        column.AddChild(walletRow);
        var coinGlyph = IconGlyph.Create(Glyph.Coin, 18f, Tok.Gold, 1.8f);
        coinGlyph.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        walletRow.AddChild(coinGlyph);
        _wallet = Ui.MakeLabel("", 22f, Tok.TextDim, HorizontalAlignment.Left);
        _wallet.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        walletRow.AddChild(_wallet);

        _gift = RewardedRow.Create(RewardedRow.AdCopy, "+1 Mark", OnGift, 48f);
        _gift.Visible = false;
        column.AddChild(_gift);

        // Silent by default: an empty 20 sp line still costs 83 px and a container
        // separation, which is exactly the budget the footer was overflowing by.
        _status = Ui.MakeLabel("", 20f, Tok.Ember, HorizontalAlignment.Left);
        _status.Visible = false;
        column.AddChild(_status);

        // Footer, two lines: the privacy link and the build string each need the
        // full 360 dp column on their own at 20 sp, and sharing one row pushed the
        // build number off the right edge.
        var footer = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        footer.AddThemeConstantOverride("separation", Tok.I(2f));
        column.AddChild(footer);

        _privacy = Ui.MakeButton("", TogglePrivacy, BtnKind.Ghost, 44f, 20f, "ui.toggle");
        _privacy.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        footer.AddChild(_privacy);

        _version = Ui.MakeLabel("", 18f, Tok.Muted, HorizontalAlignment.Left);
        footer.AddChild(_version);

        _pause = PauseOverlay.AttachTo(this, "Quit game", () => GetTree().Quit());
    }

    private static Control Fill(Control child)
    {
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        centre.AddChild(child);
        return centre;
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
    /// silently loses the listeners after the first visit. OnEnter/OnExit are
    /// invoked by SceneRouter only once every autoload is ready.
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
        var cfg = ConfigService.Instance.Config;

        bool runActive = RunService.Instance.ForgesInRun > 0 && !RunService.Instance.IsRunOver;
        if (runActive)
        {
            int forgeNumber = RunService.Instance.ForgesInRun + 1;
            Ui.SetCtaText(_primary,
                "CONTINUE RUN",
                $"Forge {forgeNumber} of {cfg.Heat.MaxForgesPerRun}  ·  Heat {RunService.Instance.Heat:0}");
        }
        else
        {
            Ui.SetCtaText(_primary, "LIGHT THE FORGE",
                $"Last run  ·  {save.LifetimeForges} forges  ·  {save.TotalStars} stars");
        }

        _daily.Disabled = !ProgressionService.Instance.IsDailyForgeAvailable(GameServices.UtcDay);
        _dailyLabel.Text = "Daily Forge";
        _dailyLabel.AddThemeColorOverride("font_color", _daily.Disabled ? Tok.Muted : Tok.Text);
        // The subtitle always says something: an empty second line is a layout hole.
        _dailyNote.Text = _daily.Disabled ? "Done today — back tomorrow" : "One forge a day";
        _dailyNote.AddThemeColorOverride("font_color", _daily.Disabled ? Tok.A(Tok.Muted, 0.7f) : Tok.TextDim);
        _streakLabel.Text = save.CurrentStreak > 0 ? $"{save.CurrentStreak}-day streak" : "";

        _build.Refresh(RunService.Instance.OwnedRunes, 3, false);
        bool hasBuild = RunService.Instance.OwnedRunes.Count > 0;
        _buildCaption.Visible = hasBuild;
        _build.Visible = hasBuild;

        _stats.Text = $"Atlas {save.HighestForgeUnlocked}/{ProgressionService.Instance.CatalogueSize}" +
                      $"   ·   Codex {save.DiscoveredRunes.Count}/{cfg.Runes.Count}";

        _wallet.Text = $"{EconomyService.Instance.Coins:N0} coins   ·   {EconomyService.Instance.Marks} Marks";

        // R_GIFT is a day-2+ affordance (GDD 8.2) and always opt-in.
        _gift.Visible = save.LifetimeForges >= 2
                        && AdManager.Instance.CanOfferRewarded("R_GIFT", out _);

        _status.Visible = _status.Text.Length > 0;
        _version.Text = $"build {ConfigService.Instance.ConfigVersion}";
        // UI-25: this is a CONTROL, not a status readout. Before the player has
        // chosen, the label is the action ("Privacy"); afterwards it states the
        // choice. It must never say "not set" - to a player that reads as a defect
        // in the app rather than as a decision they have not made yet.
        _privacy.Text = !save.ConsentResolved
            ? "Privacy"
            : save.ConsentStatus == "non_personalized"
                ? "Privacy: non-personalized"
                : "Privacy: personalized";
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

        // UI-21: a run already in progress resumes instead of restarting. Starting
        // a fresh run over it would silently delete the player's build.
        if (!daily && RunService.Instance.ForgesInRun > 0 && !RunService.Instance.IsRunOver)
        {
            ResumeRun();
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

    private void ResumeRun()
    {
        int index = RunService.Instance.ForgesInRun;
        EventBus.Cue("run.start");
        if (RunService.Instance.IsDraftForge)
        {
            SceneRouter.Instance.GoToRuneDraft(new DraftPayload(Mathf.Max(0, index - 1)));
            return;
        }
        ForgeData? forge = ProgressionService.Instance.ForgeFor(index);
        if (forge is null) { SceneRouter.Instance.GoToRunEnd(null); return; }
        SceneRouter.Instance.GoToForge(forge, index, RunService.Instance.Effects);
    }

    private async void OnGift()
    {
        if (_gift.IsLoading) return;
        _gift.SetLoading(true);
        try
        {
            bool granted = await AdManager.Instance.TryRewardedAsync("R_GIFT", 0);
            if (granted)
            {
                _gift.FlashGranted();
                AnalyticsService.Instance.Record("gift_claim");
            }
            Refresh();
        }
        finally
        {
            _gift.SetLoading(false);
        }
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
