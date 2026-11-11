using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Ads;
using LexiForge.Core;

namespace LexiForge.Autoload;

/// <summary>
/// Coins and Marks. Balances live in SaveService.Data (single owner), so a
/// pause/flush can never lose a grant. Every mutation emits through EventBus;
/// no view reference is ever held here.
///
/// The ONLY path an ad can credit anything is GrantFromAd, called exclusively
/// after the SDK's OnUserEarnedReward. The Guid rewardToken makes a replayed
/// callback idempotent — a double-credit here would break the 30-day economy
/// projection at the source.
///
/// Division of labour: AdManager owns ad-IMPRESSION counters (daily totals,
/// per-placement caps, cooldowns) and persists them BEFORE calling here;
/// EconomyService owns the CURRENCY grant and the MarksToday source cap. Mixing
/// the two double-counts.
///
/// GDD 7.2: every rewarded ad on every placement mints a Mark. The amount is
/// NOT a caller argument — MarkGrant.ForRewardedAd derives it from tuning, so
/// no view or future placement can silently opt out of the diegetic economy.
/// </summary>
public sealed partial class EconomyService : Node
{
    public static EconomyService Instance { get; private set; } = null!;

    private readonly HashSet<Guid> _consumedTokens = new();

    public int Coins => SaveService.Instance.Data.Coins;
    public int Marks => SaveService.Instance.Data.Marks;

    public override void _Ready() => Instance = this;

    /// <summary>
    /// Called from AdManager only after AdResult.Granted with a non-empty token.
    /// <paramref name="coins"/> is the placement's EXTRA coin payout (e.g. Double
    /// Haul); Marks are minted centrally by MarkGrant and are not negotiable.
    /// </summary>
    public bool GrantFromAd(AdOutcome outcome, int coins)
    {
        if (outcome.Result != AdResult.Granted) return false;
        if (outcome.RewardToken == Guid.Empty) return false;
        if (!_consumedTokens.Add(outcome.RewardToken))
        {
            GD.PushWarning($"[Economy] duplicate reward token {outcome.RewardToken} rejected.");
            return false;
        }

        SaveData save = SaveService.Instance.Data;
        // AdManager is the SINGLE owner of impression counters. It already ran
        // AdPolicy.RecordRewarded + PersistCounters before calling this method,
        // so RewardedToday / PlacementUses here would count every ad TWICE and
        // trip the daily and per-placement caps at half their configured value.
        // That ordering is also what MarkGrant expects: RewardedToday is already
        // the POST-increment count used for the 3rd-ad bonus.
        var cfg = ConfigService.Instance.Config;
        MarkGrant.Result grant = MarkGrant.ForRewardedAd(
            cfg.Marks, save.AdDay.RewardedToday, save.AdDay.MarksToday);

        if (coins != 0) AddCoins(coins, outcome.PlacementId);
        if (grant.MarksGranted > 0)
        {
            AddMarks(grant.MarksGranted, outcome.PlacementId);
            save.AdDay.MarksToday = grant.MarksTodayAfter;
        }
        SaveService.Instance.MarkDirty();
        EventBus.Instance.EmitSignal(EventBus.SignalName.RewardGranted,
                                     outcome.PlacementId, outcome.RewardToken.ToString());
        AnalyticsService.Instance.Record("ad_reward", outcome.PlacementId, coins.ToString(),
                                         grant.MarksGranted.ToString());
        return true;
    }

    public void AddCoins(int delta, string source)
    {
        var save = SaveService.Instance.Data;
        save.Coins = Math.Max(0, save.Coins + delta);
        SaveService.Instance.MarkDirty();
        EventBus.Instance.EmitSignal(EventBus.SignalName.CoinsChanged, delta, save.Coins, source);
    }

    public bool TrySpendCoins(int amount, string sink)
    {
        if (amount <= 0 || amount > Coins) return false;
        AddCoins(-amount, sink);
        AnalyticsService.Instance.Record("coin_spend", sink, amount.ToString());
        return true;
    }

    public void AddMarks(int delta, string source)
    {
        var save = SaveService.Instance.Data;
        save.Marks = Math.Max(0, save.Marks + delta);
        SaveService.Instance.MarkDirty();
        EventBus.Instance.EmitSignal(EventBus.SignalName.MarksChanged, delta, save.Marks, source);
    }

    public bool TrySpendMarks(int amount, string sink)
    {
        if (amount <= 0 || amount > Marks) return false;
        AddMarks(-amount, sink);
        AnalyticsService.Instance.Record("mark_spend", sink, amount.ToString());
        return true;
    }
}
