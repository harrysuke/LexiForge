using System;
using System.Collections.Generic;

namespace LexiForge.Ads;

/// <summary>Pure C# — mirrored by design/balance_config.json. Unit-testable without a scene tree.</summary>

public enum AdUnit { Rewarded, Interstitial, AppOpen, Banner }

public sealed class AdPlacementRule
{
    public required string Id { get; init; }
    public AdUnit Unit { get; init; }
    public int MinLifetimeForge { get; init; }
    public int MinSession { get; init; }
    public long CooldownSeconds { get; init; }
    public int DailyCap { get; init; }
    public bool RequiresOptIn { get; init; }
    public bool VisibleAdLabel { get; init; }
    public bool Enabled { get; init; } = true;
}

public sealed class AdPolicyConfig
{
    public int FirstInterstitialForge { get; init; } = 12;
    public int FirstInterstitialSession { get; init; } = 2;
    public int InterstitialEveryNForges { get; init; } = 4;
    public long InterstitialMinGapSeconds { get; init; } = 120;
    public int InterstitialMaxPerDay { get; init; } = 3;
    public long SuppressWithinSecondsOfRewarded { get; init; } = 90;
    public int RewardedSoftCapPerUserPerDay { get; init; } = 8;
    public int AppOpenMinSessions { get; init; } = 3;
    public long AppOpenMinGapSeconds { get; init; } = 14400;
    public int AppOpenMaxPerDay { get; init; } = 1;
    public bool BannerEnabled { get; init; }

    /// <summary>
    /// Remote-config kill switch. Named for what it DOES, not for the control:
    /// the previous `InterstitialKillSwitch = true` read as "kill ads" but actually
    /// meant "ads enabled", which is a dangerous inversion for a compliance lever.
    /// </summary>
    public bool InterstitialEnabled { get; init; } = true;
}

public sealed class AdPolicyState
{
    public int LifetimeForges { get; set; }
    public int Sessions { get; set; }
    public long LastInterstitialAt { get; set; }
    public long LastRewardedAt { get; set; }
    public long LastAppOpenAt { get; set; }
    public int InterstitialsToday { get; set; }
    public int RewardedToday { get; set; }
    public int AppOpensToday { get; set; }
    public int ForgesSinceLastInterstitial { get; set; }
    public bool IsForgePassOwner { get; set; }
    public bool InRun { get; set; }
    public bool IsDailyForge { get; set; }
    public bool ForgeWasAbandoned { get; set; }
    public bool HasUnclaimedReward { get; set; }
    [Obsolete("Use PlacementUsesToday; a HashSet cannot express a numeric cap.")]
    public HashSet<string> UsedPlacementCounts { get; } = new();

    /// <summary>placementId -> grants today. Enforces AdPlacementRule.DailyCap.</summary>
    public Dictionary<string, int> PlacementUsesToday { get; } = new(StringComparer.Ordinal);
}

public static class AdPolicy
{
    /// <summary>All suppression rules must pass. Returns a reason string for analytics on denial.</summary>
    public static bool CanShowInterstitial(AdPolicyState s, AdPolicyConfig c, long nowUtc, out string reason)
    {
        if (!c.InterstitialEnabled) { reason = "kill_switch"; return false; }
        if (s.IsForgePassOwner) { reason = "forge_pass"; return false; }
        if (s.LifetimeForges < c.FirstInterstitialForge) { reason = "new_player_grace"; return false; }
        if (s.Sessions < c.FirstInterstitialSession) { reason = "first_session"; return false; }
        if (s.InRun) { reason = "during_run"; return false; }
        if (s.IsDailyForge) { reason = "daily_forge"; return false; }
        if (s.ForgeWasAbandoned) { reason = "after_abandon"; return false; }
        if (s.HasUnclaimedReward) { reason = "unclaimed_reward"; return false; }
        if (s.InterstitialsToday >= c.InterstitialMaxPerDay) { reason = "daily_cap"; return false; }
        if (s.ForgesSinceLastInterstitial < c.InterstitialEveryNForges) { reason = "forge_frequency"; return false; }
        if (nowUtc - s.LastInterstitialAt < c.InterstitialMinGapSeconds) { reason = "cooldown"; return false; }
        if (nowUtc - s.LastRewardedAt < c.SuppressWithinSecondsOfRewarded) { reason = "near_rewarded"; return false; }
        reason = "ok";
        return true;
    }

    public static bool CanShowRewarded(string placementId, AdPolicyState s, AdPolicyConfig c,
                                       IReadOnlyDictionary<string, AdPlacementRule> rules, long nowUtc, out string reason)
    {
        if (!rules.TryGetValue(placementId, out var rule)) { reason = "unknown_placement"; return false; }
        if (!rule.Enabled || rule.Unit != AdUnit.Rewarded) { reason = "disabled"; return false; }
        if (s.LifetimeForges < rule.MinLifetimeForge) { reason = "not_unlocked"; return false; }
        if (s.Sessions < rule.MinSession) { reason = "session_gate"; return false; }
        if (nowUtc - s.LastRewardedAt < rule.CooldownSeconds) { reason = "cooldown"; return false; }
        if (rule.DailyCap > 0 && s.PlacementUsesToday.GetValueOrDefault(placementId) >= rule.DailyCap)
        {
            reason = "placement_cap"; return false;
        }
        if (s.RewardedToday >= c.RewardedSoftCapPerUserPerDay) { reason = "soft_cap_hide_entry"; return false; }
        reason = "ok";
        return true;
    }

    public static bool CanShowAppOpen(AdPolicyState s, AdPolicyConfig c, long nowUtc, out string reason)
    {
        if (s.IsForgePassOwner) { reason = "forge_pass"; return false; }
        if (s.Sessions < c.AppOpenMinSessions) { reason = "too_few_sessions"; return false; }
        if (s.InRun) { reason = "resumed_run"; return false; }
        if (s.AppOpensToday >= c.AppOpenMaxPerDay) { reason = "daily_cap"; return false; }
        if (nowUtc - s.LastAppOpenAt < c.AppOpenMinGapSeconds) { reason = "cooldown"; return false; }
        reason = "ok";
        return true;
    }

    public static void RecordInterstitial(AdPolicyState s, long nowUtc)
    {
        s.LastInterstitialAt = nowUtc;
        s.InterstitialsToday++;
        s.ForgesSinceLastInterstitial = 0;
    }

    /// <param name="placementId">Optional so the balance simulator's existing 2-arg call stays valid.</param>
    public static void RecordRewarded(AdPolicyState s, long nowUtc, string? placementId = null)
    {
        s.LastRewardedAt = nowUtc;
        s.RewardedToday++;
        if (!string.IsNullOrEmpty(placementId))
            s.PlacementUsesToday[placementId] = s.PlacementUsesToday.GetValueOrDefault(placementId) + 1;
    }

    public static void RecordAppOpen(AdPolicyState s, long nowUtc)
    {
        s.LastAppOpenAt = nowUtc;
        s.AppOpensToday++;
    }

    public static void ResetDaily(AdPolicyState s)
    {
        s.InterstitialsToday = 0;
        s.RewardedToday = 0;
        s.AppOpensToday = 0;
        s.PlacementUsesToday.Clear();
    }
}

public enum AdResult { Granted, Cancelled, NoFill, Failed }

public readonly record struct AdOutcome(AdResult Result, string PlacementId, Guid RewardToken);

/// <summary>
/// Compiled fallback placement table. Pure C# so BalanceConfig, the simulator,
/// LevelGen and the game all resolve the same numeric caps. ConfigService overlays
/// balance_config.json (and any signed remote-config payload) onto these rules.
/// </summary>
public static class DefaultPlacements
{
    public static Dictionary<string, AdPlacementRule> Build() => new(StringComparer.Ordinal)
    {
        ["R_HINT"]    = new() { Id = "R_HINT",    Unit = AdUnit.Rewarded,     MinLifetimeForge = 3,  MinSession = 1, CooldownSeconds = 30,    DailyCap = 0, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_DOUBLE"]  = new() { Id = "R_DOUBLE",  Unit = AdUnit.Rewarded,     MinLifetimeForge = 1,  MinSession = 1, CooldownSeconds = 60,    DailyCap = 0, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_MARK"]    = new() { Id = "R_MARK",    Unit = AdUnit.Rewarded,     MinLifetimeForge = 5,  MinSession = 2, CooldownSeconds = 60,    DailyCap = 6, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_COOL"]    = new() { Id = "R_COOL",    Unit = AdUnit.Rewarded,     MinLifetimeForge = 1,  MinSession = 1, CooldownSeconds = 120,   DailyCap = 0, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_FREEZE"]  = new() { Id = "R_FREEZE",  Unit = AdUnit.Rewarded,     MinLifetimeForge = 3,  MinSession = 2, CooldownSeconds = 86400, DailyCap = 1, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_GIFT"]    = new() { Id = "R_GIFT",    Unit = AdUnit.Rewarded,     MinLifetimeForge = 1,  MinSession = 2, CooldownSeconds = 21600, DailyCap = 2, RequiresOptIn = true, VisibleAdLabel = true },
        ["R_REFILL"]  = new() { Id = "R_REFILL",  Unit = AdUnit.Rewarded,     MinLifetimeForge = 1,  MinSession = 3, CooldownSeconds = 86400, DailyCap = 1, RequiresOptIn = true, VisibleAdLabel = true },
        ["I_RUN_END"] = new() { Id = "I_RUN_END", Unit = AdUnit.Interstitial, MinLifetimeForge = 12, MinSession = 2, CooldownSeconds = 120,   DailyCap = 3, RequiresOptIn = false, VisibleAdLabel = true },
        ["A_OPEN"]    = new() { Id = "A_OPEN",    Unit = AdUnit.AppOpen,      MinLifetimeForge = 0,  MinSession = 3, CooldownSeconds = 14400, DailyCap = 1, RequiresOptIn = false, VisibleAdLabel = true },
        ["B_HOME"]    = new() { Id = "B_HOME",    Unit = AdUnit.Banner,       Enabled = false },
    };
}
