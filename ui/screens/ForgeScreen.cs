using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The playable forge. This scene is instantiated ONCE and pooled by
/// SceneRouter.GoToForge - ResetFor rebinds the pure-C# ForgeSession to the
/// existing HUD / grid / rack / overlay nodes (GDD 11.5 forbids reloading it).
///
/// Layout is the three-band model from UI_VISUAL_SYSTEM section 5.1, resolved
/// against the real viewport instead of a fixed 800 dp assumption:
///
///   Read   (top)     Heat bar, wallet, Flow, jar, slot progress  - read only
///   Solve  (middle)  the crossword                               - absorbs variance
///   Act    (bottom)  compose strip, rack, action row             - thumb reach
///
/// The Read band anchors top and the Act band anchors bottom, so every dp of
/// height variance lands in the Solve band. A layout that only works at 800 dp is
/// a layout that breaks on the cheapest device in the target set (D-01).
///
/// Ownership: this view is the ONLY place Godot input, signals and async ad
/// callbacks are translated into ForgeSession calls. All rules (Flow, keystone
/// gating, hints, nudges, coin maths, stars) live in core/ and are unit-tested.
/// </summary>
public sealed partial class ForgeScreen : Control, IForgeScreen
{
    private const double UiRefreshSeconds = 0.25;

    private Control _stage = null!;
    private ForgeHud _hud = null!;
    private WordGrid _grid = null!;
    private LetterRack _rack = null!;
    private Toast _toast = null!;
    private ResultsCard _results = null!;
    private HintSheet _hints = null!;
    private PauseOverlay _pause = null!;
    private GiveUpModal _giveUp = null!;

    private ForgeSession? _session;
    private ForgeData? _forge;
    private ForgeOutcome? _outcome;
    private RunEndInfo? _runEnd;
    private Layout _layout = Layout.For(1920f, 1080f);
    private int _runIndex;
    private double _heatBefore;
    private double _uiAccumulator;
    private bool _adBusy;
    private bool _doubled;
    private bool _revealOffered;
    private bool _firstPickRecorded;
    private ulong _forgeOpenedMs;
    private int _lastBonusCount;
    private bool _jarBurstDone;

    public override void _Ready()
    {
        Tok.Setup(GetViewportRect().Size.X);

        var background = new ColorRect { Color = Tok.BgBase, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        // The stage exists so the keystone pulse can scale the whole screen
        // without disturbing the pause sheet or the ad frame.
        _stage = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_stage);
        Ui.FullRect(_stage);

        _hud = new ForgeHud { MouseFilter = MouseFilterEnum.Ignore };
        _stage.AddChild(_hud);

        _grid = new WordGrid();
        _stage.AddChild(_grid);

        _rack = new LetterRack();
        _stage.AddChild(_rack);

        _toast = new Toast();
        _stage.AddChild(_toast);

        _results = new ResultsCard();
        _stage.AddChild(_results);
        Ui.FullRect(_results);

        _hints = new HintSheet();
        _stage.AddChild(_hints);
        Ui.FullRect(_hints);

        _pause = PauseOverlay.AttachTo(this, "Leave run — this forge starts over", LeaveRun,
                                       () => SceneRouter.Instance.GoHome());

        // Sibling of the pause overlay, NOT a child of _stage: _stage is scaled
        // by the keystone pulse, and an answer should never wobble. GiveUpModal
        // calls MoveToFront() on Open(), so it wins whichever overlay opened last
        // did. ForgeScreen guards against two overlays being open at once.
        _giveUp = new GiveUpModal();
        AddChild(_giveUp);

        _rack.WordSubmitted += OnWordSubmitted;
        _rack.ShuffleRequested += () => AnalyticsService.Instance.Record("shuffle");
        _rack.HintRequested += OpenHintSheet;
        _rack.TilePicked += OnTilePicked;
        _hud.PauseRequested += () => { if (!_giveUp.IsOpen) _pause.Toggle(); };
        _hud.GiveUpRequested += OpenGiveUp;
        _hints.CoinRequested += OnCoinHint;
        _hints.AdRequested += OnRewardedHint;
        _results.ContinueRequested += ContinueAfterResults;
        _results.DoubleHaulRequested += OnDoubleHaul;
        _results.JarBurstRequested += OnJarBurst;
        _giveUp.KeepForgingRequested += CloseGiveUp;
        _giveUp.RestartRequested += RestartAfterGiveUp;
        _giveUp.LeaveRunRequested += LeaveRunAfterGiveUp;

        Resized += LayoutBands;
        LayoutBands();

        // D-02: the banner reserve exists in the layout and is asserted, even
        // though v1 ships it disabled.
        if (OS.IsDebugBuild()) AdSafeFrame.AssertClear(this);
    }

    /// <summary>
    /// Reserves the three bands from the real viewport. Called on _Ready, on
    /// resize, and on every ResetFor (the bank size decides the Act band height).
    /// </summary>
    private void LayoutBands()
    {
        if (_hud is null) return;

        float width = Size.X > 1 ? Size.X : GetViewportRect().Size.X;
        float height = Size.Y > 1 ? Size.Y : GetViewportRect().Size.Y;
        if (width < 1 || height < 1) return;

        _layout = Layout.For(height, width);

        float hudHeight = Tok.Dp(156f);
        int tiles = _session?.Forge.LetterBank.Length + _session?.Effects.WildTiles ?? 4;
        float rackWidthDp = (width - Tok.Dp(Tok.MarginDp) * 2f) / Tok.Scale;
        float rackHeight = Tok.Dp(LetterRack.RequiredHeightDp(_layout, tiles, rackWidthDp));

        // Read band: anchored to the top, never compressed below 148 dp. 156 dp
        // gives the three HUD rows their measured minimum plus the 10 dp heat bar.
        _hud.SetAnchorsPreset(LayoutPreset.TopWide);
        _hud.OffsetLeft = 0;
        _hud.OffsetRight = 0;
        _hud.OffsetTop = 0;
        _hud.OffsetBottom = hudHeight;

        // Act band: anchored to the bottom, never compressed below 264 dp.
        _rack.SetAnchorsPreset(LayoutPreset.BottomWide);
        _rack.OffsetLeft = Tok.Dp(Tok.MarginDp);
        _rack.OffsetRight = -Tok.Dp(Tok.MarginDp);
        _rack.OffsetTop = -rackHeight;
        _rack.OffsetBottom = 0;

        // Solve band: everything left over.
        _grid.SetAnchorsPreset(LayoutPreset.FullRect);
        _grid.OffsetLeft = Tok.Dp(Tok.MarginDp);
        _grid.OffsetRight = -Tok.Dp(Tok.MarginDp);
        _grid.OffsetTop = hudHeight;
        _grid.OffsetBottom = -rackHeight;

        _grid.ApplyLayout(_layout);
        _rack.ApplyLayout(_layout);

        // The toast lives in the empty gutter directly above the rack. Anchoring it
        // to a fixed 180 dp put it on top of the first grid row at Compact height.
        _toast.PlaceAbove(rackHeight);
    }

    public void OnEnter(object? payload)
    {
        _pause.Close();
        _hints.Close();
        _giveUp.Close();
        // A drag-submit can complete the forge, at which point _Process stops
        // updating InputBusy. Clear it so the results card's opt-in ad is not
        // silently rejected as "input busy".
        AdManager.Instance.InputBusy = false;
        AdManager.Instance.SetUnclaimedReward(false);
    }

    public void OnExit()
    {
        _pause.Close();
        _hints.Close();
        _giveUp.Close();
        AdManager.Instance.InputBusy = false;
    }

    public void OnBack()
    {
        // Back is the SAME decision as KEEP FORGING: a stuck player must never
        // be trapped behind a card with one exit.
        if (_giveUp.IsOpen) { CloseGiveUp(); return; }
        // Never pause on top of the results card: the player must be able to
        // collect their reward and continue.
        if (_results.IsOpen) return;
        if (_hints.IsOpen) { _hints.Close(); return; }
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
        _revealOffered = false;
        _firstPickRecorded = false;
        _jarBurstDone = false;
        _lastBonusCount = 0;
        _uiAccumulator = 0;
        _heatBefore = RunService.Instance.Heat;
        _forgeOpenedMs = Time.GetTicksMsec();

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
        _results.Close();
        _pause.Close();
        _hints.Close();
        _giveUp.Close();

        UpdateHintBadge();
        LayoutBands();

        EventBus.Instance.EmitSignal(EventBus.SignalName.ForgeStarted, forge.ForgeId, runIndex);
        AnalyticsService.Instance.Record("forge_start", forge.ForgeId.ToString(), runIndex.ToString());
    }

    public override void _Process(double delta)
    {
        // While the answer card is up, the forge clock stops: revealing the words
        // must not silently cost the player par time, a nudge, or a struggle offer.
        if (_session is null || _results.IsOpen || _giveUp.IsOpen) return;

        _session.Tick(delta);
        // Ads must never be loaded mid-gesture (GDD 11.5).
        AdManager.Instance.InputBusy = _rack.IsDragging;

        // Throttled: HUD text formatting allocates strings, so it must not run 60
        // times per second on a low-end Android device.
        _uiAccumulator += delta;
        if (_uiAccumulator < UiRefreshSeconds) return;
        _uiAccumulator = 0;

        // UI-37: three wrong attempts in a row offer a free reveal as a soft
        // button under the rack - no modal, no confirm, no cost.
        if (_session.ConsecutiveWrongAttemptsReached && !_revealOffered)
        {
            _revealOffered = true;
            _rack.ShowRevealOffer(OnRevealOffer);
        }

        // UI-36: the 45 s nudge is framed as the player's own perception. The
        // letter breathes in place on the rack; there is no toast and the word
        // "free" never appears.
        if (_session.TryGetNudge(out char letter, out int slot, out _))
        {
            _rack.HighlightLetter(letter);
            EventBus.Cue("nudge.reveal");
            AnalyticsService.Instance.Record("nudge_shown",
                ((int)_session.ElapsedSeconds).ToString(), slot.ToString());
        }

        _grid.Refresh();
        _hud.Refresh(_session);
    }

    private void OnTilePicked(int index)
    {
        if (_firstPickRecorded) return;
        _firstPickRecorded = true;
        // The one number that measures friction at session start (section 10).
        long elapsed = (long)(Time.GetTicksMsec() - _forgeOpenedMs);
        AnalyticsService.Instance.Record("ui_first_interactive", elapsed.ToString());
    }

    // -------------------------------------------------------------- submission
    private void OnWordSubmitted(string word)
    {
        if (_session is null || _results.IsOpen) { _rack.ClearSelection(); return; }

        Vector2? origin = _rack.SelectionCenterGlobal();

        // Pre-submit Flow decides whether a penalty actually broke a streak.
        int flowBefore = (int)Math.Round(_session.Flow * 100);
        string rawWord = word;
        WordSubmission submission = _session.Submit(word);

        // Debug-only autopsy for "the game rejected the word it just revealed".
        // Shows the EXACT string the rack built (a nearby tile can join a drag),
        // the verdict, and whether the trie knows that word — enough to tell a
        // stale words_en.dat from a mis-spelled gesture without guessing.
        if (OS.IsDebugBuild() && !submission.IsSuccess)
            GD.Print($"[WORD] raw='{rawWord}' len={rawWord.Length} norm='{submission.Word}' kind={submission.Kind} " +
                     $"repeat={submission.IsRepeat} bank='{_session.Forge.LetterBank}' " +
                     $"answer='{_session.Forge.KeystoneWord}' " +
                     $"inTrie={GameDataService.Instance.Dictionary.Contains(rawWord.ToLowerInvariant())} " +
                     $"gaps={GameDataService.Instance.DictionaryGaps.Count}");

        // ONLY a repeat keeps the tiles. It is not a spelling mistake: the player
        // can add the missing rune instead of rebuilding the word. This is the
        // recovery path for a 3-tile drag that auto-submitted a prefix of a
        // 4-letter answer (the CARE report). Successes, real penalties and the
        // other "not yet" verdicts keep their existing clearing behaviour.
        if (!submission.IsRepeat) _rack.ClearSelection();
        _grid.Refresh();
        _hud.Refresh(_session);

        if (submission.IsSuccess)
        {
            _rack.ClearNudge();
            EmitWordCue(submission);
            if (submission.Kind == WordSubmissionKind.SolutionWord)
            {
                _toast.Show($"FORGED {submission.Word.ToUpperInvariant()}", Tok.Jade);
            }
            else
            {
                // E3: the coin arcs INTO the jar from the word the player built.
                _lastBonusCount = submission.BonusWords;
                _hud.CoinIntoJar(origin ?? _grid.GetGlobalRect().GetCenter());
                _toast.Show($"+{ConfigService.Instance.Config.Economy.PerBonusWord} coins  ·  Bonus Jar", Tok.Gold);
                AnalyticsService.Instance.Record("jar_fill", submission.BonusWords.ToString());
                if (!_jarBurstDone && submission.BonusWords >= ConfigService.Instance.Config.Economy.BonusWordCap)
                {
                    _jarBurstDone = true;
                    OnJarBurst();
                }
            }

            EventBus.Instance.EmitSignal(EventBus.SignalName.WordAccepted,
                submission.Word, (int)Math.Round(submission.Flow * 100), submission.BonusWords);
            AnalyticsService.Instance.Record("word_ok", submission.Kind.ToString(), submission.Word);

            if (_forge is not null &&
                string.Equals(submission.Word, _forge.KeystoneWord, StringComparison.OrdinalIgnoreCase))
                PlayKeystone();
        }
        else
        {
            // UI-P2: rejection is information, not punishment. The rack wobbles
            // 6 dp, the strip turns Rust, one quiet dot. No screen shake.
            _rack.Reject(rawWord);
            if (submission.IsPenalty && flowBefore > 115) EventBus.Cue("flow.break");
            // UI-66: name the exact word the rack built. A generic "Already
            // forged" is indistinguishable from "the answer I was just shown was
            // refused" when the player actually submitted a 3-letter prefix.
            _toast.Show(ReasonFor(submission, rawWord), Tok.RustText);
            if (submission.IsPenalty)
            {
                EventBus.Instance.EmitSignal(EventBus.SignalName.WordRejected, submission.Kind.ToString());
                AnalyticsService.Instance.Record("word_bad", submission.Kind.ToString());
            }
            // Repeats are deliberately NOT penalties, so WordRejected / word_bad
            // never fire for them. Without this line a duplicate is invisible in
            // telemetry even though the player saw a rejection toast.
            else if (submission.IsRepeat)
                AnalyticsService.Instance.Record("word_repeat", submission.Kind.ToString(), submission.Word);
        }

        if (submission.ForgeCompleted && _session.Outcome is not null) ShowResults();
    }

    /// <summary>
    /// Kind-specific word feedback. AudioEventBridge deliberately cannot do this:
    /// WordAccepted does not say solution vs bonus vs keystone, so the view that
    /// owns the submission emits the precise cue (AUDIO_DESIGN section 4.2 rule 3).
    /// </summary>
    private void EmitWordCue(in WordSubmission submission)
    {
        bool keystone = _forge is not null &&
                        string.Equals(submission.Word, _forge.KeystoneWord, StringComparison.OrdinalIgnoreCase);
        if (keystone)
        {
            // The aha. Priority 3, never voice-stolen, ducks music 4 dB.
            EventBus.Cue("word.keystone");
            return;
        }
        if (submission.Kind == WordSubmissionKind.SolutionWord)
        {
            EventBus.Cue("word.solution", (int)Math.Round(submission.Flow * 100));
            EventBus.Cue("tile.place", submission.Word.Length);
            return;
        }
        EventBus.Cue("word.bonus");
    }

    /// <summary>
    /// The keystone sequence (section 8.3) - the ONLY event allowed the full loud
    /// budget: gold sweep, 1.5% screen pulse, double haptic. Input stays live
    /// throughout (UI-16): the player can start the next word mid-celebration.
    /// </summary>
    private void PlayKeystone()
    {
        _grid.PlayKeystoneSweep();

        if (!UiAnim.Reduced)
        {
            _stage.PivotOffset = _stage.Size * 0.5f;
            Tween tween = UiAnim.On(_stage, "pulse");
            tween.TweenProperty(_stage, "scale", new Vector2(1.015f, 1.015f), 0.12f)
                 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.Out);
            tween.TweenProperty(_stage, "scale", Vector2.One, 0.16f)
                 .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        }

        UiAnim.Haptic(30);
        GetTree().CreateTimer(0.09, false).Timeout += () => UiAnim.Haptic(18);
    }

    /// <summary>
    /// UI-66: a rejection names the exact word the rack built and never borrows
    /// the language of a DIFFERENT outcome. "Forged" belongs to solution words
    /// (the success toast says "FORGED ARC"); a repeated bonus word goes in the
    /// jar. Collapsing both into "Already forged" is what made a stray 3-tile
    /// drag read as "the game refused the CARE it just revealed to me".
    /// </summary>
    private static string ReasonFor(in WordSubmission submission, string rawWord)
    {
        string word = string.IsNullOrEmpty(rawWord) ? "" : rawWord.ToUpperInvariant();
        return submission.Kind switch
        {
            WordSubmissionKind.Invalid or WordSubmissionKind.TooShort => "Use at least 3 letters",
            WordSubmissionKind.NotConstructible => "Only the rune tiles on the rack",
            WordSubmissionKind.NotInDictionary => $"{word} · not in the dictionary",
            WordSubmissionKind.AlreadyFound => $"{word} · already forged",
            WordSubmissionKind.AlreadyInJar => $"{word} · already in the jar",
            WordSubmissionKind.BonusJarFull => $"Bonus Jar is full · {word} not added",
            WordSubmissionKind.KeystoneLocked => "Fill every other word first",
            WordSubmissionKind.ForgeAlreadyComplete => "Forge complete",
            _ => "Try another word"
        };
    }

    // ------------------------------------------------------------------ hints
    private void OpenHintSheet()
    {
        if (_session is null || _results.IsOpen) return;
        var cfg = ConfigService.Instance.Config;
        int hintsLeft = Mathf.Max(0, cfg.Economy.MaxHintsPerForge - _session.HintsUsed);
        if (hintsLeft == 0)
        {
            EventBus.Cue("ui.disabled");
            _toast.Show("No more reveals for this forge", Tok.Muted);
            return;
        }

        int cost = EconomyModel.HintCost(_session.HintsUsed + 1, cfg.Economy);
        bool adVisible = SaveService.Instance.Data.LifetimeForges + 1
                         >= cfg.Progression.FirstSession.RewardedEntryPointsVisibleFromForge
                         && AdManager.Instance.CanOfferRewarded("R_HINT", out _);
        _hints.Open(cost, _session.FreeHintsRemaining, hintsLeft, adVisible);
    }

    private void OnCoinHint()
    {
        _hints.Close();
        ApplyHint(HintSource.Coin);
    }

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
            EventBus.Cue("ui.disabled");
            _toast.Show(hint.Reason switch
            {
                "insufficient_coins" => "Not enough coins for a reveal",
                "max_hints_reached" => "No more reveals for this forge",
                _ => "Nothing left to reveal"
            }, Tok.Muted);
            return;
        }

        if (hint.CoinCost > 0) EconomyService.Instance.TrySpendCoins(hint.CoinCost, "forge_hint");
        RevealLetter(hint.Letter, hint.SlotIndex, source.ToString());
    }

    private void RevealLetter(char letter, int slotIndex, string source)
    {
        _rack.ClearNudge();
        _grid.Refresh();
        _hud.Refresh(_session!);
        UpdateHintBadge();
        _toast.Show($"Revealed {char.ToUpperInvariant(letter)}", Tok.Jade);
        EventBus.Instance.EmitSignal(EventBus.SignalName.HintGranted,
            slotIndex, char.ToUpperInvariant(letter).ToString());
        AnalyticsService.Instance.Record("hint", source);
        AnalyticsService.Instance.Record("slot_reveal_count",
            (_session!.SolutionWordCount - _session.FoundSolutions.Count).ToString());
    }

    private void UpdateHintBadge()
    {
        if (_session is null) return;
        var cfg = ConfigService.Instance.Config;
        int left = Mathf.Max(0, cfg.Economy.MaxHintsPerForge - _session.HintsUsed);
        _rack.SetHint(left, _session.FreeHintsRemaining > 0);
    }

    /// <summary>
    /// Opt-in only. The ad grant resolves on the main thread (AndroidAdService
    /// completes its Task inline from the SDK signal), so it is safe to touch the
    /// session here. A no-fill leaves the coin hint working and says so neutrally.
    /// </summary>
    private async void OnRewardedHint()
    {
        if (_session is null || _results.IsOpen || _adBusy) return;
        _adBusy = true;
        _hints.SetAdLoading(true);
        try
        {
            bool granted = await AdManager.Instance.TryRewardedAsync("R_HINT", 0);
            if (granted && _session is not null && !_results.IsOpen)
            {
                _hints.FlashAdGranted();
                _hints.Close();
                ApplyHint(HintSource.Ad);
            }
            else
            {
                // Section 9.4: neutral copy, no error, no retry prompt.
                _hints.SetAdLoading(false);
                _hints.Close();
                _toast.Show("Sponsor unavailable — the coin reveal still works", Tok.Muted);
            }
        }
        finally
        {
            _adBusy = false;
            _hints.SetAdLoading(false);
        }
    }

    private void OnRevealOffer()
    {
        if (_session is null) return;
        if (!_session.TryGrantStruggleReveal(out char letter, out int slot, out _)) return;
        EventBus.Cue("nudge.reveal");
        RevealLetter(letter, slot, "struggle");
    }

    // --------------------------------------------------------------- give up
    /// <summary>
    /// GDD 6.2.1 / UI-64. Show every word, change NO gameplay state. The modal only
    /// reads ForgeData and ForgeSession, so KEEP FORGING restores the exact same
    /// slots, rack, Flow, jar, hints and elapsed clock.
    /// </summary>
    private void OpenGiveUp()
    {
        if (_session is null || _forge is null || _giveUp.IsOpen) return;
        // One overlay at a time. A results card is a reward moment; the hint and
        // pause sheets already own the screen while open.
        if (_results.IsOpen || _hints.IsOpen || _pause.IsOpen) return;

        _rack.ClearSelection();
        _rack.HideRevealOffer();
        AdManager.Instance.InputBusy = false;
        _giveUp.Open(_forge, _session);
        AnalyticsService.Instance.Record("give_up_opened",
            _forge.ForgeId.ToString(),
            _session.FoundSolutions.Count.ToString(),
            _session.SolutionWordCount.ToString(),
            ((int)_session.ElapsedSeconds).ToString());
    }

    private void CloseGiveUp()
    {
        if (!_giveUp.IsOpen) return;
        _giveUp.Close();
        AnalyticsService.Instance.Record("give_up_kept");
    }

    /// <summary>
    /// A fresh attempt at the SAME puzzle. ResetFor re-emits forge_start, which is
    /// honest — a new attempt really did start. Heat, run index, coins, stars and
    /// LifetimeForges are untouched because no forge was completed.
    /// </summary>
    private void RestartAfterGiveUp()
    {
        if (_forge is null) return;
        int forgeId = _forge.ForgeId;
        _giveUp.Close();
        ResetFor(_forge, _runIndex, RunService.Instance.Effects);
        // GDD 8.3: no interstitial after an abandon/retry. ProgressionService
        // clears this flag on the next completed forge.
        AdManager.Instance.SetAbandoned(true);
        AnalyticsService.Instance.Record("give_up_restart", forgeId.ToString());
    }

    private void LeaveRunAfterGiveUp()
    {
        _giveUp.Close();
        AnalyticsService.Instance.Record("give_up_leave");
        LeaveRun();
    }

    // ---------------------------------------------------------------- results
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

        // The seam is the only place Heat is discussed numerically (E5).
        _hud.SetHeat(RunService.Instance.Heat, RunService.Instance.RunScore, true);

        bool canDouble = AdManager.Instance.CanOfferRewarded("R_DOUBLE", out _);
        _results.Open(_outcome, _heatBefore, RunService.Instance.Heat, canDouble);
        AdManager.Instance.SetUnclaimedReward(canDouble);
        AnalyticsService.Instance.Record("forge_complete", _outcome.ForgeId.ToString(),
            _outcome.Result.Stars.ToString(), _outcome.TotalCoins.ToString());
    }

    private void OnJarBurst()
    {
        _hud.BurstJar();
        AnalyticsService.Instance.Record("jar_burst",
            ConfigService.Instance.Config.Economy.PerBonusWord.ToString());
        UiAnim.Haptic(20);
    }

    private async void OnDoubleHaul()
    {
        if (_outcome is null || _doubled || _adBusy) return;
        _adBusy = true;
        _results.SetAdLoading(true);
        try
        {
            int extra = _outcome.TotalCoins;
            bool granted = await AdManager.Instance.TryRewardedAsync("R_DOUBLE", extra);
            if (!granted)
            {
                EventBus.Cue("ui.disabled");
                _results.NoteAdUnavailable();
                return;
            }
            _doubled = true;
            _results.MarkDoubled(_outcome.TotalCoins + extra);
            AdManager.Instance.SetUnclaimedReward(false);
            AnalyticsService.Instance.Record("double_haul", extra.ToString());
        }
        finally
        {
            _adBusy = false;
            _results.SetAdLoading(false);
        }
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
