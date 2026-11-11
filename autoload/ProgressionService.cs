using System;
using Godot;
using LexiForge.Core;

namespace LexiForge.Autoload;

/// <summary>
/// Forge sequencing, stars, packs and the deterministic Daily Forge seed.
/// P0 ships 20 generated forges, so NextForgeId cycles through the catalogue
/// instead of dead-ending at forge 20 — the launch catalogue is 400 (GDD 12).
/// </summary>
public sealed partial class ProgressionService : Node
{
    public static ProgressionService Instance { get; private set; } = null!;

    public override void _Ready() => Instance = this;

    public int CatalogueSize => GameDataService.Instance.Forges.Count;

    public int NextForgeId
    {
        get
        {
            int count = CatalogueSize;
            if (count == 0) return 1;
            int wanted = Math.Max(1, SaveService.Instance.Data.HighestForgeUnlocked);
            return ((wanted - 1) % count) + 1;
        }
    }

    public ForgeData? CurrentForge => GameDataService.Instance.ForgeById(NextForgeId);

    /// <summary>
    /// Deterministic forge for a position inside a run. P0 ships 20 forges, so a
    /// run of up to 8 (+cool-forge continuations) walks the catalogue from the
    /// player's unlocked position and wraps instead of dead-ending.
    /// </summary>
    public ForgeData? ForgeFor(int runIndex)
    {
        var forges = GameDataService.Instance.Forges;
        if (forges.Count == 0) return null;
        int start = Math.Max(1, SaveService.Instance.Data.HighestForgeUnlocked) - 1;
        int offset = Math.Max(0, runIndex);
        return forges[(start + offset) % forges.Count];
    }

    public PackBand? BandFor(int forgeId) => ConfigService.Instance.Config.BandFor(forgeId);

    public int StarsFor(int forgeId)
        => SaveService.Instance.Data.StarsByForge.TryGetValue(forgeId, out int stars) ? stars : 0;

    public int TotalStars => SaveService.Instance.Data.TotalStars;

    public void RecordForge(int forgeId, int stars, int coins)
    {
        SaveService.Instance.RecordForge(forgeId, stars);
        EconomyService.Instance.AddCoins(coins, $"forge_{forgeId}");
        AdManager.Instance.SyncFromSave();
        AdManager.Instance.OnForgeCompleted();
        AdManager.Instance.SetAbandoned(false);
        EventBus.Instance.EmitSignal(EventBus.SignalName.ForgeCompleted, forgeId, coins, stars);
        SaveService.Instance.Flush();
    }

    /// <summary>Stable per UTC day, so every player sees the same Daily Forge.</summary>
    public int DailyForgeSeed()
    {
        var cfg = ConfigService.Instance.Config;
        int days = (int)(DateTime.UtcNow.Date - cfg.Progression.DailyForgeSeedEpoch.Date).TotalDays;
        return unchecked(cfg.Progression.WeeklyGildedForgeCount * 104729 + days * 7919);
    }

    public bool IsDailyForgeAvailable(int utcDay)
        => SaveService.Instance.Data.LastDailyForgeDay != utcDay;
}
