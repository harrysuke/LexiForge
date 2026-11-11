using System;

namespace LexiForge.Core;

/// <summary>
/// Pure, Godot-free Sponsor Mark arithmetic for rewarded ads (GDD 7.2 / 7.3).
///
/// Design invariant: EVERY rewarded ad on EVERY placement mints a Sponsor Mark.
/// That rule belongs here, not at each call site. An earlier P0 build let views
/// pass a `marks` argument and all three passed 0, which silently made the
/// Patron Board economy inert and invalidated the 30-day Monte Carlo projection
/// (~1.0 Mark/DAU/day, ~3.2 per ad-engaged user). Centralising it means a new
/// placement cannot forget to mint, and the shipped game plus the offline
/// simulator/tests all resolve the same numbers.
///
/// Ordering contract, mirroring BalanceSim.MintRewarded exactly: the ad
/// impression counter is incremented BEFORE this is evaluated, so
/// <paramref name="rewardedToday"/> is the POST-increment count of the day and
/// the bonus fires on the configured Nth ad (not on every Nth ad).
/// </summary>
public static class MarkGrant
{
    public readonly record struct Result(int MarksGranted, int MarksTodayAfter);

    /// <param name="cfg">Tuning read from balance_config.json (marks.sources.rewardedAd).</param>
    /// <param name="rewardedToday">Rewarded impressions today, already incremented for this ad.</param>
    /// <param name="marksToday">Marks already minted from ads today.</param>
    public static Result ForRewardedAd(MarkConfig cfg, int rewardedToday, int marksToday)
    {
        int cap = Math.Max(0, cfg.DailyCapFromAds);
        int already = Math.Max(0, marksToday);
        int allowed = Math.Max(0, cap - already);

        // Base mint is universal and placement-independent. Clamp to the
        // remaining daily allowance so a 6/day cap can never be exceeded.
        int granted = Math.Min(Math.Max(0, cfg.PerRewardedAd), allowed);

        // One-time bonus on the configured Nth rewarded ad of the day. Equality
        // (not modulo) matches the simulator's "+1 on the 3rd ad". The bonus is
        // inside the cap too: `granted < allowed` guarantees at most one extra
        // and never pushes MarksToday past DailyCapFromAds.
        if (cfg.BonusMarkEveryNAd > 0 &&
            rewardedToday == cfg.BonusMarkEveryNAd &&
            granted < allowed)
            granted++;

        return new Result(granted, already + granted);
    }
}
