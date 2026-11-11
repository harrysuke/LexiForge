using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LexiForge.Ads;

/// <summary>
/// The ONLY surface game code touches. Mediation/provider changes never reach gameplay.
/// Android impl lives in AndroidAdService.cs; EditorAdService is used for all day-to-day
/// design iteration so nobody ever needs a live ad network to test a forge.
/// </summary>
public interface IAdService
{
    bool IsRewardedReady(string placementId);
    void Preload(string placementId);
    Task<AdOutcome> ShowRewardedAsync(string placementId);
    bool TryShowInterstitial(string placementId);
    bool TryShowAppOpen();
    event Action<AdOutcome>? Outcome;
}

/// <summary>
/// Deterministic mock: force success / cancel / no-fill from the editor Inspector
/// or a debug key. Never blocks on a network.
/// </summary>
public sealed class EditorAdService : IAdService
{
    public AdResult ForcedResult { get; set; } = AdResult.Granted;
    public int SimulatedDelayMs { get; set; } = 250;

    private readonly HashSet<string> _ready = new();
    public event Action<AdOutcome>? Outcome;

    public bool IsRewardedReady(string placementId) => _ready.Contains(placementId);

    public void Preload(string placementId) => _ready.Add(placementId);

    public async Task<AdOutcome> ShowRewardedAsync(string placementId)
    {
        _ready.Add(placementId);
        if (SimulatedDelayMs > 0) await Task.Delay(SimulatedDelayMs);

        // Only a granted impression carries a reward token, matching the real
        // bridge: a cancelled/no-fill outcome must never be creditable even if
        // a caller forgets to check Result first.
        var outcome = new AdOutcome(ForcedResult, placementId,
                                    ForcedResult == AdResult.Granted ? Guid.NewGuid() : Guid.Empty);
        Outcome?.Invoke(outcome);
        return outcome;
    }

    public bool TryShowInterstitial(string placementId)
    {
        var outcome = new AdOutcome(ForcedResult, placementId, Guid.Empty);
        Outcome?.Invoke(outcome);
        // Interstitials have no "earned reward" concept: only a genuinely shown
        // impression may return true. Returning true for Cancelled/Failed would
        // let AdManager record a phantom interstitial and burn the 3/day cap
        // during editor testing without any ad ever appearing.
        return ForcedResult == AdResult.Granted;
    }

    public bool TryShowAppOpen() => TryShowInterstitial("A_OPEN");
}

// DefaultPlacements moved to AdTypes.cs (pure C#) so BalanceConfig, LevelGen,
// BalanceSim and CoreTests can all resolve the same numeric caps without
// pulling in the Godot-facing IAdService implementation.
