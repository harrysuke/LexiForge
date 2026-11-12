using System;
using System.Threading.Tasks;
using Godot;
using LexiForge.Ads;
using LexiForge.Ads.Android;

namespace LexiForge.Autoload;

/// <summary>
/// Thin Godot adapter over the PURE C# AdPolicy. All caps, cooldowns and
/// suppression rules live in AdTypes.cs and are already verified by CoreTests;
/// this class only chooses a platform implementation and marshals callbacks.
///
/// Compliance invariants (GDD 8):
///  - Rewarded ads are strictly opt-in and the ONLY grant path is the
///    OnUserEarnedReward result, never ad-close/app-resume.
///  - Grant is idempotent through EconomyService's rewardToken set.
///  - Forge Pass / the remote kill switch remove interstitials and app-open,
///    never opt-in rewarded value.
///  - No ad is ever loaded during a drag gesture (SceneRouter sets InputBusy).
/// </summary>
public sealed partial class AdManager : Node
{
    public static AdManager Instance { get; private set; } = null!;

    private readonly AdPolicyState _state = new();
    private IAdService _service = null!;
    private int _mainThreadId;

    /// <summary>Set by the forge view while a tile drag is in progress.</summary>
    public bool InputBusy { get; set; }

    public override void _Ready()
    {
        // _Ready always runs on Godot's main thread. Capture it so an ad service
        // that completes its Task off-thread can be marshalled back before any
        // EventBus signal or scene-tree access occurs.
        _mainThreadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        Instance = this;
        _service = OS.GetName() == "Android" && AndroidAdService.Instance is not null
            ? AndroidAdService.Instance
            : new EditorAdService();
        _service.Outcome += OnAdOutcome;
        SyncFromSave();
        // Preload every placement the P0 UI can actually request. Loading on
        // first tap races the SDK's async load against show_rewarded and shows
        // up to the player as a spurious no-fill.
        _service.Preload("R_HINT");
        _service.Preload("R_DOUBLE");
        _service.Preload("R_COOL");
    }

    /// <summary>Mirrors persistent counters into the pure-C# policy state.</summary>
    public void SyncFromSave()
    {
        var save = SaveService.Instance.Data;
        _state.LifetimeForges = save.LifetimeForges;
        _state.Sessions = save.Sessions;
        _state.IsForgePassOwner = save.ForgePassOwned;
        _state.InterstitialsToday = save.AdDay.InterstitialsToday;
        _state.RewardedToday = save.AdDay.RewardedToday;
        _state.AppOpensToday = save.AdDay.AppOpensToday;
        _state.PlacementUsesToday.Clear();
        foreach (var (id, count) in save.AdDay.PlacementUses)
            _state.PlacementUsesToday[id] = count;
    }

    public void SetInRun(bool inRun) => _state.InRun = inRun;

    /// <summary>
    /// AdPolicy's interstitial frequency gate needs a forge counter. It is a
    /// per-session transient (RecordInterstitial resets it), so it is not part
    /// of the persisted AdDayState.
    /// </summary>
    public void OnForgeCompleted() => _state.ForgesSinceLastInterstitial++;
    public void SetDailyForge(bool daily) => _state.IsDailyForge = daily;
    public void SetAbandoned(bool abandoned) => _state.ForgeWasAbandoned = abandoned;
    public void SetUnclaimedReward(bool unclaimed) => _state.HasUnclaimedReward = unclaimed;

    public bool CanOfferRewarded(string placementId, out string reason)
        => AdPolicy.CanShowRewarded(placementId, _state, ConfigService.Instance.AdPolicy,
                                   ConfigService.Instance.Placements, Now(), out reason);

    /// <summary>
    /// Player-initiated. A no-fill returns false silently; the coin path stays
    /// available. <paramref name="coins"/> is the placement's extra coin payout.
    /// The universal Sponsor Mark mint is NOT a parameter: GDD 7.2 requires
    /// every rewarded ad on every placement to mint one, so EconomyService
    /// derives it from tuning via MarkGrant (verified by CoreTests).
    /// </summary>
    public async Task<bool> TryRewardedAsync(string placementId, int coins)
    {
        if (InputBusy) { GD.Print("[Ads] input busy — not starting a load mid-gesture."); return false; }
        long now = Now();
        if (!AdPolicy.CanShowRewarded(placementId, _state, ConfigService.Instance.AdPolicy,
                                      ConfigService.Instance.Placements, now, out string reason))
        {
            GD.Print($"[Ads] {placementId} not offered: {reason}");
            AnalyticsService.Instance.Record("ad_blocked", placementId, reason);
            return false;
        }

        // Ad creative owns the audio channel for the impression. The finally
        // guarantees the -30 dB duck is released on grant, cancel, no-fill and
        // exception alike. AudioDirector.SetAdPlayback is idempotent, so the
        // bridge's release on RewardGranted is a safe no-op.
        AudioDirector.Instance?.SetAdPlayback(true);
        AdOutcome outcome;
        try
        {
            outcome = await _service.ShowRewardedAsync(placementId);
        }
        finally
        {
            AudioDirector.Instance?.SetAdPlayback(false);
        }

        // IAdService implementations are NOT required to complete on the main
        // thread: EditorAdService uses Task.Delay (thread-pool continuation) and
        // some mediation SDKs marshal callbacks from a binder thread. Everything
        // below — and the caller's UI refresh — emits EventBus signals and
        // touches Controls, which is only legal on Godot's main thread.
        // ProcessFrame is emitted by the main loop, so awaiting it hops back.
        if (System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThreadId)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        if (outcome.Result != AdResult.Granted) return false;

        // The Android bridge reports the request, not the placement. Normalise
        // here so the cap ledger and the reward signal always carry the real id.
        outcome = outcome with { PlacementId = placementId };
        AdPolicy.RecordRewarded(_state, now, placementId);
        PersistCounters();
        return EconomyService.Instance.GrantFromAd(outcome, coins);
    }

    public void ShowInterstitialIfAllowed(string placementId)
    {
        if (InputBusy) return;
        long now = Now();
        if (!AdPolicy.CanShowInterstitial(_state, ConfigService.Instance.AdPolicy, now, out string reason))
        {
            AnalyticsService.Instance.Record("interstitial_blocked", placementId, reason);
            return;
        }
        if (!_service.TryShowInterstitial(placementId)) return;
        // Only a real full-screen impression needs the duck; the editor mock
        // shows nothing, and ducking there would just confuse play-testing.
        // Released on focus-in (SceneRouter/AdManager resume) or by the
        // AudioDirector watchdog if the SDK never reports a close.
        if (OS.GetName() == "Android") AudioDirector.Instance?.SetAdPlayback(true);
        AdPolicy.RecordInterstitial(_state, now);
        PersistCounters();
    }

    public void TryShowAppOpen()
    {
        long now = Now();
        if (!AdPolicy.CanShowAppOpen(_state, ConfigService.Instance.AdPolicy, now, out _)) return;
        if (!_service.TryShowAppOpen()) return;
        if (OS.GetName() == "Android") AudioDirector.Instance?.SetAdPlayback(true);
        AdPolicy.RecordAppOpen(_state, now);
        PersistCounters();
    }

    public void OnNewDay()
    {
        AdPolicy.ResetDaily(_state);
        SyncFromSave();
    }

    private void PersistCounters()
    {
        var day = SaveService.Instance.Data.AdDay;
        day.InterstitialsToday = _state.InterstitialsToday;
        day.RewardedToday = _state.RewardedToday;
        day.AppOpensToday = _state.AppOpensToday;
        day.PlacementUses.Clear();
        foreach (var (id, count) in _state.PlacementUsesToday) day.PlacementUses[id] = count;
        SaveService.Instance.MarkDirty();
    }

    private void OnAdOutcome(AdOutcome outcome)
    {
        // Analytics ONLY. Granting happens exclusively in TryRewardedAsync after
        // the earned-reward callback; ad-close events must never credit anything.
        AnalyticsService.Instance.Record("ad_outcome", outcome.PlacementId, outcome.Result.ToString());
    }

    private static long Now() => (long)Time.GetUnixTimeFromSystem();

    public override void _Notification(int what)
    {
        base._Notification(what);
        long notification = what;
        if (notification is Node.NotificationApplicationPaused or Node.NotificationApplicationFocusOut)
            SetInRun(false);
    }
}
