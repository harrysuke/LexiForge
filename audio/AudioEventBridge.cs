using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.Audio;

/// <summary>
/// The ONLY translation layer between gameplay and sound. Views emit semantic
/// signals; this bridge decides what (if anything) should be heard. No screen
/// or service ever names an audio file, a bus or a volume — so a sound can be
/// retuned, renamed or replaced without touching gameplay code.
///
/// Two channels:
///  * EventBus game signals  -> state changes, economy, Heat, music states.
///  * EventBus.FeedbackCue   -> high-resolution tactile cues (tile pick, word
///    kind, free nudge). cueId IS the audio_events.json event id; magnitude is
///    the semantic intensity consumed by ladder pitch / variation selection.
///
/// WordAccepted / WordRejected intentionally do NOT play SFX here: they lack
/// the submission kind (solution vs bonus vs keystone). ForgeScreen emits a
/// FeedbackCue with that context; those two signals only drive adaptive music.
/// </summary>
public sealed partial class AudioEventBridge : Node
{
    private int _flowHundredths = 100;
    private int _heat;

    // Collision tracking: two "big gesture" cues landing on the same frame turn
    // the mix to mud, so a follow-up cue stands down while the first still rings.
    private ulong _lastCueMs;
    private string _lastCueId = "";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _heat = (int)Math.Round(RunService.Instance.Heat);

        EventBus bus = EventBus.Instance;
        bus.FeedbackCue += OnFeedbackCue;
        bus.ScreenChanged += OnScreenChanged;
        bus.ForgeStarted += OnForgeStarted;
        bus.ForgeCompleted += OnForgeCompleted;
        bus.WordAccepted += OnWordAccepted;
        bus.WordRejected += OnWordRejected;
        bus.HeatChanged += OnHeatChanged;
        bus.HintGranted += OnHintGranted;
        bus.CoinsChanged += OnCoinsChanged;
        bus.MarksChanged += OnMarksChanged;
        bus.RewardGranted += OnRewardGranted;
        bus.DraftOffered += OnDraftOffered;
        bus.RunEnded += OnRunEnded;
    }

    public override void _ExitTree()
    {
        EventBus bus = EventBus.Instance;
        bus.FeedbackCue -= OnFeedbackCue;
        bus.ScreenChanged -= OnScreenChanged;
        bus.ForgeStarted -= OnForgeStarted;
        bus.ForgeCompleted -= OnForgeCompleted;
        bus.WordAccepted -= OnWordAccepted;
        bus.WordRejected -= OnWordRejected;
        bus.HeatChanged -= OnHeatChanged;
        bus.HintGranted -= OnHintGranted;
        bus.CoinsChanged -= OnCoinsChanged;
        bus.MarksChanged -= OnMarksChanged;
        bus.RewardGranted -= OnRewardGranted;
        bus.DraftOffered -= OnDraftOffered;
        bus.RunEnded -= OnRunEnded;
    }

    // ------------------------------------------------------------- cue channel
    private void OnFeedbackCue(string cueId, int magnitude)
    {
        _lastCueId = cueId;
        _lastCueMs = Time.GetTicksMsec();
        AudioDirector.Instance.Play(cueId, magnitude);
    }

    /// <summary>True when <paramref name="cueId"/> was the last cue played and
    /// it is still within its gesture window.</summary>
    private bool RecentCue(string cueId, ulong withinMs)
        => _lastCueId == cueId && Time.GetTicksMsec() - _lastCueMs < withinMs;

    // ----------------------------------------------------------- game signals
    private void OnScreenChanged(string screenName)
    {
        AudioDirector.Instance.Play("screen.whoosh");
        AudioDirector.Instance.SetMusicState(screenName);
    }

    private void OnForgeStarted(int forgeId, int runIndex)
    {
        _flowHundredths = 100;
        // A run that just opened its forge door already said "begin"; the board
        // settle would only muddy the attack (AUDIO_DESIGN.md section 6.4).
        if (!RecentCue("run.start", 500)) AudioDirector.Instance.Play("forge.start");
        AudioDirector.Instance.SetMusicState("Forge");
        AudioDirector.Instance.SetMusicIntensity(_flowHundredths, _heat);
    }

    private void OnForgeCompleted(int forgeId, int coins, int stars)
    {
        // ProgressionService emits this after the canonical Heat/stars pass, so
        // the cadence lands exactly on the results card, never before it.
        // If the final word was the keystone, its 900 ms bell cluster IS the
        // cadence; stacking forge.complete on top would smear both.
        if (!RecentCue("word.keystone", 800))
        {
            AudioDirector.Instance.Play("forge.complete");
            if (stars == 3) AudioDirector.Instance.Play("forge.perfect");
        }
    }

    private void OnWordAccepted(string word, int flowHundredths, int bonusWords)
    {
        _flowHundredths = flowHundredths;
        AudioDirector.Instance.SetMusicIntensity(_flowHundredths, _heat);
    }

    private void OnWordRejected(string reason)
    {
        // Adaptive music ONLY. The SFX for a rejected word (word.reject, plus
        // flow.break when a streak actually broke) is emitted by ForgeScreen,
        // which knows the submission kind and the pre-submit Flow. This signal
        // carries neither, and stacking a second resolve here would double every
        // rejection (AUDIO_DESIGN.md section 4.5).
        _flowHundredths = 100;
        AudioDirector.Instance.SetMusicIntensity(_flowHundredths, _heat);
    }

    private void OnHeatChanged(int newHeat)
    {
        int previous = _heat;
        _heat = newHeat;
        AudioDirector.Instance.SetMusicIntensity(_flowHundredths, _heat);

        foreach (int threshold in AudioDirector.Instance.Table.Music.HeatThresholds)
            if (previous < threshold && newHeat >= threshold)
                AudioDirector.Instance.Play("heat.threshold", newHeat);

        // Cool the forge: a genuine drop, not the ordinary per-forge Heat gain.
        if (previous - newHeat >= 10) AudioDirector.Instance.Play("heat.cool");
    }

    private void OnHintGranted(int slotIndex, string letter)
        => AudioDirector.Instance.Play("hint.reveal");

    private void OnCoinsChanged(int delta, int newBalance, string source)
    {
        if (delta >= 20) AudioDirector.Instance.Play("coin.earn");
    }

    private void OnMarksChanged(int delta, int newBalance, string source)
    {
        // Every rewarded ad mints a Mark (GDD 7.2). Deliberately neutral: no
        // audio may pressure the player into watching another ad.
        if (delta > 0) AudioDirector.Instance.Play("mark.earn");
    }

    private void OnRewardGranted(string placement, string rewardToken)
    {
        AudioDirector.Instance.SetAdPlayback(false);
        AudioDirector.Instance.Play("ad.reward");
    }

    private void OnDraftOffered(string runeIdsCsv)
    {
        AudioDirector.Instance.Play("rune.draft.open");
        AudioDirector.Instance.SetMusicState("RuneDraft");
    }

    private void OnRunEnded(int totalScore, int forges, string runeIdsCsv)
    {
        AudioDirector.Instance.Play("run.end");
        AudioDirector.Instance.SetMusicState("RunEnd");
    }
}
