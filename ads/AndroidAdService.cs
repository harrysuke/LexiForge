using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using LexiForge.Ads;

namespace LexiForge.Ads.Android;

/// <summary>
/// Android adapter. Provider-agnostic on purpose: it talks to a JNISingleton
/// exported by the AdMob Android plugin through Godot's dynamic singleton API,
/// so no plugin assembly is referenced from gameplay code and mediation can be
/// swapped without recompiling the game.
///
/// Bridge contract expected from the AAR (all signals on the main thread):
///   method  initialize(string appId, bool nonPersonalized)
///   method  load_rewarded(string unitId, string requestId)
///   method  show_rewarded(string requestId)
///   method  load_interstitial(string unitId)
///   method  show_interstitial()
///   method  load_app_open(string unitId)
///   method  show_app_open()
///   signal  rewarded_earned(string requestId)  <-- the ONLY grant trigger
///   signal  rewarded_closed(string requestId)
///   signal  ad_failed(string requestId, string reason)
///
/// ARCHITECTURAL SKETCH: verify exact signal/method names against the pinned
/// plugin version before a real device build. All pure logic (caps, cooldowns,
/// idempotency) lives in AdTypes.cs and is already exact and unit-tested.
///
/// Request IDs are the bridge's correlation key. They MUST map back to a real
/// placement id: the previous sketch emitted "R_*", which silently defeated
/// per-placement daily caps and produced meaningless reward telemetry.
/// </summary>
public sealed partial class AndroidAdService : Node, IAdService
{
    private const string BridgeName = "LexiForgeAdBridge";
    private const string UnknownPlacement = "R_UNKNOWN";

    public static AndroidAdService? Instance { get; private set; }

    [Export] public string RewardedUnitId { get; set; } = "ca-app-pub-xxxxxxxxxxxxxxxx/yyyyyyyyyy";
    [Export] public string InterstitialUnitId { get; set; } = "ca-app-pub-xxxxxxxxxxxxxxxx/yyyyyyyyyy";
    [Export] public string AppOpenUnitId { get; set; } = "ca-app-pub-xxxxxxxxxxxxxxxx/yyyyyyyyyy";

    private GodotObject? _bridge;
    private readonly Dictionary<string, TaskCompletionSource<AdOutcome>> _pending = new();
    private readonly Dictionary<string, string> _requestToPlacement = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _placementToRequest = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ready = new(StringComparer.Ordinal);
    private bool _consentResolved;

    public event Action<AdOutcome>? Outcome;

    public override void _Ready()
    {
        Instance = this;
        // Autoload runs on desktop too; without the singleton the game must stay
        // fully playable offline rather than throwing on every ad entry point.
        if (!Engine.HasSingleton(BridgeName))
        {
            GD.PushWarning($"[Ads] {BridgeName} missing — running with no ads (offline-safe).");
            return;
        }

        _bridge = Engine.GetSingleton(BridgeName);
        _bridge.Connect("rewarded_earned", Callable.From<string>(OnRewardedEarned));
        _bridge.Connect("rewarded_closed", Callable.From<string>(OnRewardedClosed));
        _bridge.Connect("ad_failed", Callable.From<string, string>(OnAdFailed));
    }

    /// <summary>Must be called AFTER the UMP consent flow resolves. Never before.</summary>
    public void Initialize(string appId, bool nonPersonalizedAds)
    {
        if (_bridge is null || _consentResolved) return;
        _consentResolved = true;
        _bridge.Call("initialize", appId, nonPersonalizedAds);
    }

    public bool IsRewardedReady(string placementId) => _ready.Contains(placementId);

    public void Preload(string placementId) => Load(placementId);

    private void Load(string placementId)
    {
        if (_bridge is null) return;
        string requestId = Guid.NewGuid().ToString("N");
        _requestToPlacement[requestId] = placementId;
        _placementToRequest[placementId] = requestId;
        _bridge.Call("load_rewarded", RewardedUnitId, requestId);
        _ready.Add(placementId);
    }

    public Task<AdOutcome> ShowRewardedAsync(string placementId)
    {
        // Deliberately NOT RunContinuationsAsynchronously: the bridge signals are
        // emitted on Godot's MAIN thread, and AdManager's continuation performs
        // the coin grant and emits EventBus signals. Forcing continuations onto
        // the thread pool there would touch the scene tree off-thread. Completing
        // inline keeps the whole reward chain on the thread that owns the engine.
        var tcs = new TaskCompletionSource<AdOutcome>();
        if (_bridge is null)
        {
            var noFill = new AdOutcome(AdResult.NoFill, placementId, Guid.Empty);
            tcs.SetResult(noFill);
            Outcome?.Invoke(noFill);
            return tcs.Task;
        }

        if (!_placementToRequest.TryGetValue(placementId, out string? requestId) ||
            !_requestToPlacement.ContainsKey(requestId))
        {
            Load(placementId);
            requestId = _placementToRequest.GetValueOrDefault(placementId);
        }
        if (requestId is null)
        {
            var noFill = new AdOutcome(AdResult.NoFill, placementId, Guid.Empty);
            tcs.SetResult(noFill);
            return tcs.Task;
        }

        _pending[requestId] = tcs;
        _bridge.Call("show_rewarded", requestId);
        return tcs.Task;
    }

    public bool TryShowInterstitial(string placementId)
    {
        if (_bridge is null) return false;
        _bridge.Call("show_interstitial");
        return true;
    }

    public bool TryShowAppOpen()
    {
        if (_bridge is null) return false;
        _bridge.Call("show_app_open");
        return true;
    }

    /// <summary>Consumes the correlation key exactly once and returns its real placement.</summary>
    private string TakePlacement(string requestId)
    {
        if (!_requestToPlacement.Remove(requestId, out string? placement)) return UnknownPlacement;
        _placementToRequest.Remove(placement);
        _ready.Remove(placement);
        return placement;
    }

    private void Complete(string requestId, AdResult result, string? failureReason = null)
    {
        // earned -> closed is a normal SDK sequence. The second terminal callback
        // has already been consumed; ignore it instead of emitting a phantom
        // "cancelled" outcome on top of a successful grant.
        if (!_requestToPlacement.ContainsKey(requestId) && !_pending.ContainsKey(requestId)) return;

        string placement = TakePlacement(requestId);
        if (failureReason is not null)
            GD.PushWarning($"[Ads] {placement}/{requestId}: {failureReason}. Coin path stays available.");

        var outcome = new AdOutcome(result, placement,
                                    result == AdResult.Granted ? Guid.NewGuid() : Guid.Empty);
        if (_pending.Remove(requestId, out var tcs)) tcs.TrySetResult(outcome);
        Outcome?.Invoke(outcome);

        // Always have the next opt-in impression warming up, but never during a
        // drag gesture (AdManager.InputBusy is checked before ShowRewardedAsync).
        if (placement != UnknownPlacement) Load(placement);
    }

    // ---- Grant only from the SDK's "user earned reward" callback. Never on close. ----
    private void OnRewardedEarned(string requestId) => Complete(requestId, AdResult.Granted);
    private void OnRewardedClosed(string requestId) => Complete(requestId, AdResult.Cancelled);

    /// <summary>
    /// The bridge reports BOTH load failures and show failures through
    /// "ad_failed". A request with no pending ShowRewardedAsync is a PRELOAD
    /// failure. Calling Complete() there would auto-Load() again, and a
    /// persistent no-fill would produce a tight, unbounded retry loop against
    /// the ad network (a real account-level throttling/ban risk on Android).
    /// Instead drop the correlation keys, mark the placement unready, and let
    /// the next PLAYER-INITIATED tap trigger exactly one fresh load.
    /// </summary>
    private void OnAdFailed(string requestId, string reason)
    {
        if (_pending.ContainsKey(requestId))
        {
            Complete(requestId, AdResult.NoFill, reason);
            return;
        }

        string placement = TakePlacement(requestId);
        _ready.Remove(placement);
        GD.PushWarning($"[Ads] preload failed for {placement}/{requestId}: {reason}. " +
                       "Will retry on the next opt-in tap; coin path stays available.");
        Outcome?.Invoke(new AdOutcome(AdResult.NoFill, placement, Guid.Empty));
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        // Pause/resume and back-button handling are centralised in SceneRouter.
        // Never display an ad while the application is paused.
    }

    public override void _ExitTree()
    {
        if (Instance == this) Instance = null;
        base._ExitTree();
    }
}
