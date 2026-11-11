using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The playable forge. This scene is instantiated ONCE and pooled by
/// SceneRouter.GoToForge — ResetFor rebinds the pure-C# ForgeSession to the
/// existing HUD / grid / rack / overlay nodes (GDD 11.5 forbids reloading it).
///
/// Ownership: this view is the ONLY place Godot input, signals and async ad
/// callbacks are translated into ForgeSession calls. All rules (Flow, keystone
/// gating, hints, nudges, coin maths, stars) live in core/ and are unit-tested.
/// No game formula is repeated here.
/// </summary>
public sealed partial class ForgeScreen : Control, IForgeScreen
{
    private const double UiRefreshSeconds = 0.25;

    private ForgeHud _hud = null!;
    private WordGrid _grid = null!;
    private LetterRack _rack = null!;
    private ResultsCard _results = null!;
    private PauseOverlay _pause = null!;
    private Label _theme = null!;

    private ForgeSession? _session;
    private ForgeData? _forge;
    private ForgeOutcome? _outcome;
    private RunEndInfo? _runEnd;
    private int _runIndex;
    private double _heatBefore;
    private double _uiAccumulator;
    private bool _adBusy;
    private bool _doubled;

    public override void _Ready()
    {
        var background = new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 18, 48);

        _hud = new ForgeHud { CustomMinimumSize = new Vector2(0, 330) };
        _hud.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        column.AddChild(_hud);

        _theme = Ui.MakeLabel("", 30, Ui.Gold);
        column.AddChild(_theme);

        _grid = new WordGrid { SizeFlagsVertical = SizeFlags.ExpandFill };
        column.AddChild(_grid);

        _rack = new LetterRack { CustomMinimumSize = new Vector2(0, 420) };
        _rack.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        column.AddChild(_rack);

        _results = new ResultsCard();
        AddChild(_results);
        Ui.FullRect(_results);

        _pause = PauseOverlay.AttachTo(this, "Leave run", LeaveRun);

        _rack.WordSubmitted += OnWordSubmitted;
        _rack.ShuffleRequested += () => AnalyticsService.Instance.Record("shuffle");
        _hud.HintRequested += () => ApplyHint(HintSource.Coin);
        _hud.RewardedHintRequested += OnRewardedHint;
        _hud.PauseRequested += () => _pause.Toggle();
        _results.ContinueRequested += ContinueAfterResults;
        _results.DoubleHaulRequested += OnDoubleHaul;
    }

    public void OnEnter(object? payload)
    {
        _pause.Close();
        // A drag-submit can complete the forge, at which point _Process stops
        // updating InputBusy. Clear it so the results card's opt-in ad is not
        // silently rejected as "input busy".
        AdManager.Instance.InputBusy = false;
        AdManager.Instance.SetUnclaimedReward(false);
    }

    public void OnExit()
    {
        _pause.Close();
        AdManager.Instance.InputBusy = false;
    }

    public void OnBack()
    {
        // Never pause on top of the results card: the player must be able to
        // collect their reward and continue.
        if (_results.IsOpen) return;
        _pause.Toggle();
    }

    /// <summary>Rebinds pooled nodes to the next forge. No scene reload, no new nodes.</summary>
    public void ResetFor(ForgeData forge, int runIndex, RuneEffects effects)
    {
        _forge = forge;
        _runIndex = runIndex;
        _outcome = null;
        _runEnd = null;
        _doubled = false;
        _uiAccumulator = 0;
        _heatBefore = RunService.Instance.Heat;

        _session = new ForgeSession(forge, GameDataService.Instance.Dictionary,
                                    ConfigService.Instance.Config, effects);

        // Anvil (+1) plus the scripted tutorial hint on forge 4 of the very first
        // run (GDD 3.5). Both are free and teach the button before it costs coins.
        int freeHints = effects.ExtraFreeHints;
        var firstSession = ConfigService.Instance.Config.Progression.FirstSession;
        if (RunService.Instance.IsFirstRunEver && runIndex + 1 == firstSession.ScriptedFreeHintOnForge)
            freeHints++;
        _session.GrantFreeHints(freeHints);

        _grid.ResetFor(_session);
        _hud.ResetFor(_session);
        _rack.ResetFor(_session, unchecked(forge.Seed ^ (runIndex * 7919)));
        _theme.Text = $"Forge {forge.ForgeId}  ·  {forge.Theme}  ·  DI {forge.DifficultyIndex:0}";
        _results.Close();
        _pause.Close();

        EventBus.Instance.EmitSignal(EventBus.SignalName.ForgeStarted, forge.ForgeId, runIndex);
        AnalyticsService.Instance.Record("forge_start", forge.ForgeId.ToString(), runIndex.ToString());
    }

    public override void _Process(double delta)
    {
        if (_session is null || _results.IsOpen) return;

        _session.Tick(delta);
        // Ads must never be loaded mid-gesture (GDD 11.5).
        AdManager.Instance.InputBusy = _rack.IsDragging;

        // Throttled: HUD text formatting and FindHintTarget allocate strings, so
        // they must not run 60 times per second on a low-end Android device.
        _uiAccumulator += delta;
        if (_uiAccumulator < UiRefreshSeconds) return;
        _uiAccumulator = 0;

        if (_session.ConsecutiveWrongAttemptsReached &&
            _session.TryGrantStruggleReveal(out char struggleLetter, out int struggleSlot, out _))
        {
            _hud.Toast($"Free letter: {char.ToUpperInvariant(struggleLetter)} — keep going!", Ui.Good);
            AnalyticsService.Instance.Record("struggle_reveal", struggleSlot.ToString());
        }
        else if (_session.TryGetNudge(out char letter, out int slot, out _))
        {
            _hud.Toast($"Free nudge: {char.ToUpperInvariant(letter)}", Ui.Good);
            AnalyticsService.Instance.Record("nudge", slot.ToString());
        }

        _grid.Refresh();
        _hud.Refresh(_session);
    }

    private void OnWordSubmitted(string word)
    {
        if (_session is null || _results.IsOpen) { _rack.ClearSelection(); return; }

        WordSubmission submission = _session.Submit(word);
        _rack.ClearSelection();
        _grid.Refresh();
        _hud.Refresh(_session);

        if (submission.IsSuccess)
        {
            string message = submission.Kind == WordSubmissionKind.SolutionWord
                ? $"FORGED {submission.Word.ToUpperInvariant()}  ·  Flow {submission.Flow:0.00}x"
                : $"+{ConfigService.Instance.Config.Economy.PerBonusWord} coins  ·  Bonus Jar";
            _hud.Toast(message, Ui.Good);
            EventBus.Instance.EmitSignal(EventBus.SignalName.WordAccepted,
                submission.Word, (int)Math.Round(submission.Flow * 100), submission.BonusWords);
            AnalyticsService.Instance.Record("word_ok", submission.Kind.ToString(), submission.Word);
        }
        else
        {
            string reason = ReasonFor(submission.Kind);
            _hud.Toast(reason, Ui.Bad);
            if (submission.IsPenalty)
            {
                EventBus.Instance.EmitSignal(EventBus.SignalName.WordRejected, submission.Kind.ToString());
                AnalyticsService.Instance.Record("word_bad", submission.Kind.ToString());
            }
        }

        if (submission.ForgeCompleted && _session.Outcome is not null) ShowResults();
    }

    private static string ReasonFor(WordSubmissionKind kind) => kind switch
    {
        WordSubmissionKind.Invalid or WordSubmissionKind.TooShort => "Use at least 3 letters",
        WordSubmissionKind.NotConstructible => "Only the rune tiles on the rack",
        WordSubmissionKind.NotInDictionary => "Not in the dictionary",
        WordSubmissionKind.AlreadyFound => "Already forged",
        WordSubmissionKind.BonusJarFull => "Bonus Jar is full",
        WordSubmissionKind.KeystoneLocked => "Fill every other word first",
        WordSubmissionKind.ForgeAlreadyComplete => "Forge complete",
        _ => "Try another word"
    };

    // ---------------------------------------------------------------- hints
    private void ApplyHint(HintSource source)
    {
        if (_session is null || _results.IsOpen) return;

        // UseHint validates the balance FIRST; only then is anything debited, so
        // a rejected hint can never cost coins (no refund path needed).
        HintResult hint = _session.UseHint(
            source == HintSource.Coin && _session.FreeHintsRemaining > 0 ? HintSource.Scripted : source,
            EconomyService.Instance.Coins);

        if (!hint.Granted)
        {
            _hud.Toast(hint.Reason switch
            {
                "insufficient_coins" => "Not enough coins for a hint",
                "max_hints_reached" => "No more hints for this forge",
                _ => "Nothing left to reveal"
            }, Ui.Bad);
            return;
        }

        if (hint.CoinCost > 0) EconomyService.Instance.TrySpendCoins(hint.CoinCost, "forge_hint");
        RevealLetter(hint.Letter, hint.SlotIndex, source.ToString());
    }

    private void RevealLetter(char letter, int slotIndex, string source)
    {
        _grid.Refresh();
        _hud.Refresh(_session!);
        _hud.Toast($"Revealed {char.ToUpperInvariant(letter)}", Ui.Good);
        EventBus.Instance.EmitSignal(EventBus.SignalName.HintGranted,
            slotIndex, char.ToUpperInvariant(letter).ToString());
        AnalyticsService.Instance.Record("hint", source);
    }

    /// <summary>
    /// Opt-in only. The ad grant resolves on the main thread (AndroidAdService
    /// completes its Task inline from the SDK signal), so it is safe to touch
    /// the session here. A no-fill leaves the coin hint working.
    /// </summary>
    private async void OnRewardedHint()
    {
        if (_session is null || _results.IsOpen || _adBusy) return;
        _adBusy = true;
        try
        {
            bool granted = await AdManager.Instance.TryRewardedAsync("R_HINT", 0);
            if (granted && _session is not null && !_results.IsOpen)
                ApplyHint(HintSource.Ad);
            else
                _hud.Toast("Ad unavailable — coin hint still works.", Ui.Muted);
        }
        finally { _adBusy = false; }
    }

    // -------------------------------------------------------------- results
    private void ShowResults()
    {
        if (_session?.Outcome is null || _forge is null) return;

        _outcome = _session.Outcome;
        _heatBefore = RunService.Instance.Heat;

        // RunService applies the canonical Heat formula; ProgressionService
        // records stars and credits coins exactly once.
        _runEnd = RunService.Instance.ApplyForge(_outcome, _forge.DifficultyIndex);
        ProgressionService.Instance.RecordForge(_outcome.ForgeId, _outcome.Result.Stars,
                                                _outcome.TotalCoins);

        bool canDouble = AdManager.Instance.CanOfferRewarded("R_DOUBLE", out _);
        _results.Open(_outcome, _heatBefore, RunService.Instance.Heat, canDouble);
        AdManager.Instance.SetUnclaimedReward(canDouble);
        AnalyticsService.Instance.Record("forge_complete", _outcome.ForgeId.ToString(),
            _outcome.Result.Stars.ToString(), _outcome.TotalCoins.ToString());
    }

    private async void OnDoubleHaul()
    {
        if (_outcome is null || _doubled || _adBusy) return;
        _adBusy = true;
        try
        {
            int extra = _outcome.TotalCoins;
            bool granted = await AdManager.Instance.TryRewardedAsync("R_DOUBLE", extra);
            if (!granted)
            {
                _results.NoteAdUnavailable();
                return;
            }
            _doubled = true;
            _results.MarkDoubled(_outcome.TotalCoins + extra);
            AdManager.Instance.SetUnclaimedReward(false);
            AnalyticsService.Instance.Record("double_haul", extra.ToString());
        }
        finally { _adBusy = false; }
    }

    private void ContinueAfterResults()
    {
        _results.Close();
        AdManager.Instance.SetUnclaimedReward(false);

        if (_runEnd is not null)
        {
            SceneRouter.Instance.GoToRunEnd(new RunEndPayload(_runEnd, _runIndex));
            return;
        }
        if (RunService.Instance.IsDraftForge)
        {
            SceneRouter.Instance.GoToRuneDraft(new DraftPayload(_runIndex));
            return;
        }

        int next = _runIndex + 1;
        ForgeData? forge = ProgressionService.Instance.ForgeFor(next);
        if (forge is null) { SceneRouter.Instance.GoHome(); return; }
        SceneRouter.Instance.GoToForge(forge, next, RunService.Instance.Effects);
    }

    private void LeaveRun()
    {
        AdManager.Instance.SetAbandoned(true);
        AdManager.Instance.SetInRun(false);
        SceneRouter.Instance.GoHome();
    }
}
