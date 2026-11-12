using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// A run ending is a REWARD moment, never a loss screen (GDD 3.3 / UI-02).
///
///  - The headline is a COUNT of what was made - "14 words forged" - not a verdict
///    (UI-53). Peak-end: the screen must end on a gain.
///  - The run's rune build is displayed underneath as its signature (E4).
///  - "Cool the forge" offers three options at IDENTICAL visual weight (UI-54).
///    If the ad option is privileged, the "choice" is a dark pattern and the GDD
///    charter is violated in pixels even though it is honoured in config.
///  - "End run" is a real button, not a grey text link (UI-55). Making the exit
///    small is the cheapest dark pattern in the genre.
///  - "The forge cooled", never "You failed": there is no fail state (UI-56).
/// </summary>
public sealed partial class RunEndScreen : Control, IScreen
{
    private Label _headline = null!;
    private Label _numbers = null!;
    private Label _marks = null!;
    private Label _status = null!;
    private Label _reason = null!;
    private BuildStrip _build = null!;
    private Button _adCool = null!;
    private Button _coinCool = null!;
    private Button _markCool = null!;
    private Button _endRun = null!;
    private PauseOverlay _pause = null!;
    private RunEndInfo? _info;
    private int _runIndex;
    private bool _adBusy;
    private bool _cooled;
    private bool _listening;

    public override void _Ready()
    {
        Tok.Setup(GetViewportRect().Size.X);

        var background = new ColorRect { Color = Tok.BgBase, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 12f, Tok.MarginDp);
        column.AddChild(Ui.Spacer(12f));

        _headline = Ui.Display(Ui.MakeLabel("", 30f, Tok.Text), this);
        column.AddChild(_headline);

        _build = new BuildStrip();
        _build.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(_build);

        // Two deliberate lines instead of one wrappable one: the full receipt is
        // ~1213 px at 30 sp and ~1116 px at 24 sp, so it never fits 936 px and the
        // wrap used to strand "0" at the end of the first line with "Marks" below.
        // A score/coins line plus a quieter Marks line reads like a receipt and is
        // stable for any number of digits.
        _numbers = Ui.MakeLabel("", 24f, Tok.Gold, HorizontalAlignment.Left);
        column.AddChild(_numbers);

        _marks = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left);
        column.AddChild(_marks);

        _reason = Ui.MakeLabel("", 22f, Tok.TextDim);
        column.AddChild(_reason);

        column.AddChild(Ui.Spacer(8f));

        // Three identical cards. Same 96 dp height, same fill, same border, same
        // label size. The only difference is the AD badge on the first.
        column.AddChild(Ui.MakeLabel("Cool the forge  ·  keep this run going", 20f, Tok.Muted,
                                    HorizontalAlignment.Left, wrap: true));
        var cards = Ui.MakeRow(8f);
        cards.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        column.AddChild(cards);

        _adCool = EqualCard("", OnAdCool, out var adBadge);
        adBadge.Visible = true;
        cards.AddChild(_adCool);
        _coinCool = EqualCard("", OnCoinCool, out _);
        cards.AddChild(_coinCool);
        _markCool = EqualCard("", OnMarkCool, out _);
        cards.AddChild(_markCool);

        _status = Ui.MakeLabel("", 20f, Tok.Jade, HorizontalAlignment.Center);
        column.AddChild(_status);

        column.AddChild(Ui.Spacer(8f));

        _endRun = Ui.MakeButton("End run", () => SceneRouter.Instance.GoHome(), BtnKind.Secondary, 56f, 22f, "ui.back");
        column.AddChild(_endRun);

        _pause = PauseOverlay.AttachTo(this, "Quit game", () => GetTree().Quit());
    }

    /// <summary>
    /// A cool card. The optional AD badge is the ONLY difference between the three:
    /// equal size, equal contrast, equal hit area (UI-54).
    /// </summary>
    private static Button EqualCard(string label, Action onPressed, out AdBadge badge)
    {
        Button card = Ui.MakeButton("", onPressed, BtnKind.Secondary, 96f, 20f, "ui.tap");
        card.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        card.SizeFlagsVertical = SizeFlags.ExpandFill;

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.SetAnchorsPreset(LayoutPreset.FullRect);
        column.Alignment = BoxContainer.AlignmentMode.Center;
        column.AddThemeConstantOverride("separation", Tok.I(4f));
        card.AddChild(column);

        Label text = Ui.MakeLabel(label, 20f, Tok.Text, HorizontalAlignment.Center, wrap: true);
        text.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(text);
        card.SetMeta("label", text);

        // The badge is an OVERLAY, not a second row. Stacked under the label it made
        // the ad card the only two-line card in a three-up row - a weight difference
        // where UI-54 allows exactly one: the badge itself. In the corner, all three
        // labels stay on the same baseline and the card is still marked as an ad.
        //
        // Positioned from its MEASURED width, not a fixed offset: the badge sizes
        // itself in _Ready (48 dp for "AD" at 20 sp plus its padding) and a guessed
        // 46 dp inset put its right edge 6 px outside the card.
        AdBadge created = AdBadge.Create();
        badge = created;
        created.Visible = false;
        card.AddChild(created);
        card.Resized += () => PlaceBadge(card, created);
        return card;
    }

    private static void PlaceBadge(Button card, AdBadge badge)
    {
        Vector2 size = badge.Size;
        if (size.X < 1f) return;
        badge.Position = new Vector2(card.Size.X - size.X - Tok.Dp(6f), Tok.Dp(6f));
    }

    private static void SetCardLabel(Button card, string text)
    {
        if (card.GetMeta("label").AsGodotObject() is Label label) label.Text = text;
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
        if (_headline is null) return;
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

        // UI-53: a count, not a verdict. If the count is unknown (older payload),
        // fall back to the forge count rather than inventing a number.
        int words = info.WordsForged;
        _headline.Text = words > 0 ? $"{words} words forged" : $"{info.Forges} forges forged";

        var runes = RunService.Instance.OwnedRunes;
        _build.Refresh(runes, 3, false);

        _numbers.Text = $"{score:N0}  ·  {EconomyService.Instance.Coins:N0} coins";
        _marks.Text = $"{EconomyService.Instance.Marks} Marks";

        // UI-56: never "you failed".
        bool capped = heat >= ConfigService.Instance.Config.Heat.Max - 0.01;
        _reason.Text = capped ? "The forge cooled" : "Run complete";

        var cfg = ConfigService.Instance.Config;
        SetCardLabel(_adCool, "1 ad");
        SetCardLabel(_coinCool, $"{cfg.Sinks.CoolForgeCoins} coins");
        SetCardLabel(_markCool, "1 Mark");

        bool available = !_cooled;
        _adCool.Visible = available;
        _coinCool.Visible = available;
        _markCool.Visible = available;
        _adCool.Disabled = _adBusy || !AdManager.Instance.CanOfferRewarded("R_COOL", out _);
        _coinCool.Disabled = EconomyService.Instance.Coins < cfg.Sinks.CoolForgeCoins;
        _markCool.Disabled = EconomyService.Instance.Marks < 1;
        _endRun.Visible = true;
    }

    private async void OnAdCool()
    {
        if (_cooled || _adBusy) return;
        _adBusy = true;
        try
        {
            bool granted = await AdManager.Instance.TryRewardedAsync("R_COOL", 0);
            if (granted)
            {
                AnalyticsService.Instance.Record("run_end_choice", "ad");
                Cool();
            }
            else
            {
                EventBus.Cue("ui.disabled");
                _status.Text = "Sponsor unavailable — coins and Marks still work.";
            }
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
        if (EconomyService.Instance.TrySpendCoins(cost, "cool_forge"))
        {
            AnalyticsService.Instance.Record("run_end_choice", "coins");
            Cool();
        }
        else
        {
            EventBus.Cue("ui.disabled");
            _status.Text = "Not enough coins.";
        }
    }

    private void OnMarkCool()
    {
        if (_cooled) return;
        if (EconomyService.Instance.TrySpendMarks(1, "cool_forge"))
        {
            AnalyticsService.Instance.Record("run_end_choice", "mark");
            Cool();
        }
        else
        {
            EventBus.Cue("ui.disabled");
            _status.Text = "No Sponsor Marks left.";
        }
    }

    private void Cool()
    {
        _cooled = true;
        RunService.Instance.CoolForge();
        AnalyticsService.Instance.Record("cool_forge",
            ((int)Math.Round(RunService.Instance.Heat)).ToString());
        Render();
        NextForge();
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
