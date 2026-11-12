using System;
using System.Collections.Generic;
using System.Linq;

namespace LexiForge.Core;

public enum RuneKind { Score, Utility, Economy }
public enum RuneTier { Common, Uncommon, Rare }

/// <summary>Pure C# mirror of one "runes" entry in balance_config.json.</summary>
public sealed class RuneData
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public RuneKind Kind { get; init; }
    public RuneTier Rarity { get; init; }
    public double CoinDeltaPct { get; init; }
    public double DiDelta { get; init; }
    public string Effect { get; init; } = "";
}

public sealed class RuneDraftConfig
{
    public int OptionsPerDraft { get; init; } = 3;
    public double CommonWeight { get; init; } = 0.60;
    public double UncommonWeight { get; init; } = 0.30;
    public double RareWeight { get; init; } = 0.10;
    public bool GuaranteeNonScoreOption { get; init; } = true;
    public double MaxBuildCoinDeltaPct { get; init; } = 0.30;
    public double MaxSingleRuneCoinDeltaPct { get; init; } = 0.10;
    public string[] TutorialFirstDraft { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Aggregated, bounded effect of the runes drafted this run.
///
/// Utility runes are deliberately NOT folded into the coin multiplier: they are
/// quality-of-life / difficulty modifiers. This keeps the +/-10% per rune and
/// +30% per build budget from the GDD intact and matches what BalanceSim models.
/// </summary>
public sealed class RuneEffects
{
    public double CoinDeltaPct { get; private set; }
    public double DifficultyDelta { get; private set; }
    /// <summary>Echo: coins per letter of a word that crosses an already-filled slot.</summary>
    public double EchoCoinsPerLetter { get; private set; }
    public double EmberCoinsPerLongLetter { get; private set; }
    public double BonusJarMultiplier { get; private set; } = 1.0;
    public double StarCoinMultiplier { get; private set; } = 1.0;
    public double FirstWordCoinMultiplier { get; private set; } = 1.0;
    public int ExtraFreeHints { get; private set; }
    public int WildTiles { get; private set; }
    public int CleanCoolsOnPerfect { get; private set; }
    public double HeatGainReduction { get; private set; }
    public int FreeMarksAtRunEnd { get; private set; }
    public int FreeDraftRerolls { get; private set; }
    public bool RevealSlotLengths { get; private set; }

    public static RuneEffects None => new();

    public static RuneEffects From(IEnumerable<RuneData> owned, RuneDraftConfig cfg)
    {
        var fx = new RuneEffects();
        double coin = 0, di = 0;
        foreach (var rune in owned)
        {
            coin += rune.CoinDeltaPct;
            di += rune.DiDelta;
            switch (rune.Id)
            {
                case "ember":  fx.EmberCoinsPerLongLetter = 1.0; break;
                case "echo":   fx.EchoCoinsPerLetter = 1.0; break;
                case "prism":  fx.BonusJarMultiplier = 2.0; break;
                case "anvil":  fx.ExtraFreeHints += 1; break;
                case "gale":   fx.WildTiles += 1; break;
                case "quill":  fx.CleanCoolsOnPerfect += 2; break;
                case "ledger": fx.StarCoinMultiplier = 1.25; break;
                case "mirror": fx.FreeDraftRerolls += 1; break;
                case "spark":  fx.FirstWordCoinMultiplier = 3.0; break;
                case "anchor": fx.HeatGainReduction += 3.0; break;
                case "guild":  fx.FreeMarksAtRunEnd += 1; break;
                case "lens":   fx.RevealSlotLengths = true; break;
            }
        }
        fx.CoinDeltaPct = Math.Clamp(coin, -cfg.MaxBuildCoinDeltaPct, cfg.MaxBuildCoinDeltaPct);
        fx.DifficultyDelta = Math.Max(-12.0, di);
        return fx;
    }
}

/// <summary>
/// Deterministic draft generator. Seeded per (run, forge) so a daily/replay and
/// the CI solver produce identical offers.
/// </summary>
public sealed class RuneDraft
{
    private readonly IReadOnlyList<RuneData> _catalogue;
    private readonly RuneDraftConfig _cfg;

    public RuneDraft(IReadOnlyList<RuneData> catalogue, RuneDraftConfig cfg)
    {
        _catalogue = catalogue ?? throw new ArgumentNullException(nameof(catalogue));
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        if (_catalogue.Count < _cfg.OptionsPerDraft)
            throw new ArgumentException("Rune catalogue is smaller than optionsPerDraft.");
    }

    public IReadOnlyList<RuneData> Offer(int runSeed, int forgeInRun, bool isFirstDraftEver,
                                        IReadOnlyList<string> ownedIds)
    {
        // First-ever draft is a scripted friendly teaching moment (GDD 3.5).
        if (isFirstDraftEver && _cfg.TutorialFirstDraft.Length >= _cfg.OptionsPerDraft)
        {
            var tutorial = new List<RuneData>(_cfg.OptionsPerDraft);
            foreach (string id in _cfg.TutorialFirstDraft.Take(_cfg.OptionsPerDraft))
            {
                var rune = _catalogue.FirstOrDefault(r => r.Id == id);
                if (rune is not null && !tutorial.Contains(rune)) tutorial.Add(rune);
            }
            if (tutorial.Count == _cfg.OptionsPerDraft) return tutorial;
        }

        var rng = new Random(unchecked(runSeed * 397 ^ (forgeInRun + 1) * 7919));
        var owned = new HashSet<string>(ownedIds ?? Array.Empty<string>(), StringComparer.Ordinal);
        var offer = new List<RuneData>(_cfg.OptionsPerDraft);

        for (int i = 0; i < _cfg.OptionsPerDraft; i++)
        {
            // Prefer undrafted runes; fall back to the full pool when the catalogue
            // is exhausted (runes are allowed to stack).
            var pool = _catalogue.Where(r => !owned.Contains(r.Id) && !offer.Contains(r)).ToList();
            if (pool.Count == 0) pool = _catalogue.Where(r => !offer.Contains(r)).ToList();
            if (pool.Count == 0) break;

            offer.Add(PickByRarity(pool, rng));
        }

        if (_cfg.GuaranteeNonScoreOption && offer.Count > 0 && offer.All(r => r.Kind == RuneKind.Score))
        {
            var alternative = _catalogue.FirstOrDefault(r => r.Kind != RuneKind.Score && !offer.Contains(r))
                              ?? _catalogue.First(r => r.Kind != RuneKind.Score);
            offer[^1] = alternative;
        }
        return offer;
    }

    private RuneData PickByRarity(IReadOnlyList<RuneData> pool, Random rng)
    {
        double roll = rng.NextDouble() * (_cfg.CommonWeight + _cfg.UncommonWeight + _cfg.RareWeight);
        RuneTier tier = roll < _cfg.CommonWeight ? RuneTier.Common
                      : roll < _cfg.CommonWeight + _cfg.UncommonWeight ? RuneTier.Uncommon
                      : RuneTier.Rare;

        var tierPool = pool.Where(r => r.Rarity == tier).ToList();
        if (tierPool.Count == 0) tierPool = pool.ToList();
        return tierPool[rng.Next(tierPool.Count)];
    }
}
