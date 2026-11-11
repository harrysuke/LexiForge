using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using LexiForge.Ads;

namespace LexiForge.Core;

/// <summary>
/// The shipped, validated view of design/balance_config.json.
///
/// Pure C# so the game, LevelGen, CoreTests and BalanceSim all read the SAME
/// single source of truth through the SAME clamping rules. Previously
/// ConfigService._Ready() only logged the version string and silently used
/// compiled defaults, so tuning changes in the JSON had no effect at runtime.
///
/// Rule: a missing or out-of-range value falls back to the compiled default and
/// is reported through Diagnostics. A malformed document throws — shipping a
/// silently half-parsed balance file is worse than failing the build.
/// </summary>
public sealed class ContentContract
{
    public int MinShortWordsLengthLTE4 { get; init; } = 2;
    public int KeystoneWords { get; init; } = 1;
    public int MinGridIntersections { get; init; } = 2;
    public double MaxAbandonRate { get; init; } = 0.08;
    public double MaxHintsPerForge { get; init; } = 1.2;
}

public sealed class AntiFrustrationConfig
{
    public int FreeNudgeAfterSeconds { get; init; } = 45;
    public int FreeNudgePerWindow { get; init; } = 1;
    public int FreeLetterRevealAfterWrongAttempts { get; init; } = 3;
    public bool ShuffleFreeUnlimited { get; init; } = true;
}

public sealed class PackBand
{
    public int FirstForge { get; init; }
    public int LastForge { get; init; }
    public double MinDI { get; init; }
    public double MaxDI { get; init; }
    public string Theme { get; init; } = "";
    public double TargetMedianSolveSeconds { get; init; }

    public bool Contains(double di) => di >= MinDI - 0.001 && di <= MaxDI + 0.001;
}

public sealed class FirstSessionConfig
{
    public int Forges { get; init; } = 5;
    public int Interstitials { get; init; }
    public int ScriptedFreeHintOnForge { get; init; } = 4;
    public int RewardedEntryPointsVisibleFromForge { get; init; } = 3;
}

public sealed class ProgressionConfig
{
    public int ForgesPerPack { get; init; } = 20;
    public int PacksPerChapter { get; init; } = 5;
    public int LaunchForgeCount { get; init; } = 400;
    public int AtlasScenesPerChapter { get; init; } = 20;
    public int[] StarsToUnlockAtlasPerPack { get; init; } = { 40, 60 };
    public DateTime DailyForgeSeedEpoch { get; init; } = new(2026, 1, 1);
    public int WeeklyGildedForgeCount { get; init; } = 7;
    public int FreeStreakFreezePerWeek { get; init; } = 1;
    public FirstSessionConfig FirstSession { get; init; } = new();
}

public sealed class MarkConfig
{
    public int PerRewardedAd { get; init; } = 1;
    public int DailyCapFromAds { get; init; } = 6;
    public int BonusMarkEveryNAd { get; init; } = 3;
    public int CoinEquivalent { get; init; } = 200;
}

/// <summary>
/// Coin-only alternatives for every Mark sink. Read from economy.sinks so the
/// UI never hardcodes a price the balance simulator does not know about
/// (GDD 7.3: "every Mark sink has a coin alternative").
/// </summary>
public sealed class SinkConfig
{
    public int RuneDraftRerollCoins { get; init; } = 200;
    public int CoolForgeCoins { get; init; } = 150;
    public int StreakFreezeCoins { get; init; } = 200;
}

public sealed class BalanceConfig
{
    public string SchemaVersion { get; private set; } = "1.0.0";
    public string ConfigVersion { get; private set; } = "lexiforge-0.1.0";

    public DifficultyShapeTuning Shape { get; private set; } = new();
    public DifficultyWeights Weights { get; private set; } = new();
    public ParTimeTuning ParTime { get; private set; } = new();
    public EconomyTuning Economy { get; private set; } = new();
    public HeatTuning Heat { get; private set; } = new();
    public AdPolicyConfig AdPolicy { get; private set; } = new();
    public Dictionary<string, AdPlacementRule> Placements { get; private set; } = DefaultPlacements.Build();
    public List<RuneData> Runes { get; private set; } = new();
    public RuneDraftConfig Draft { get; private set; } = new();
    public ContentContract Content { get; private set; } = new();
    public AntiFrustrationConfig AntiFrustration { get; private set; } = new();
    public ProgressionConfig Progression { get; private set; } = new();
    public MarkConfig Marks { get; private set; } = new();
    public SinkConfig Sinks { get; private set; } = new();
    public List<PackBand> PackBands { get; private set; } = new();

    /// <summary>1-based forge positions within a run that offer a rune draft.</summary>
    public int[] DraftsAfterForges { get; private set; } = { 1, 3, 5 };
    public double CoolForgeHeatDelta { get; private set; } = -25;

    /// <summary>Non-fatal fallback notices, surfaced by ConfigService at boot.</summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    private readonly List<string> _diagnostics = new();

    public static BalanceConfig Load(string path) => FromJson(File.ReadAllText(path));

    public static BalanceConfig FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        return FromElement(doc.RootElement);
    }

    public static BalanceConfig FromElement(JsonElement r)
    {
        var cfg = new BalanceConfig();
        JsonElement diff = Required(r, "difficulty");
        JsonElement econ = Required(r, "economy");
        JsonElement heat = Required(r, "heat");
        JsonElement ads = Required(r, "ads");
        JsonElement marks = Required(r, "marks");
        JsonElement prog = Required(r, "progression");
        JsonElement kpis = Required(r, "kpis");

        cfg.SchemaVersion = Str(r, "schemaVersion", cfg.SchemaVersion);
        cfg.ConfigVersion = Str(r, "configVersion", cfg.ConfigVersion);

        // ---- difficulty -------------------------------------------------
        JsonElement shape = Required(diff, "shape");
        JsonElement w = Required(diff, "weights");
        JsonElement par = Required(diff, "parTime");
        cfg.Shape = new DifficultyShapeTuning
        {
            DecayForgeConstant = D(shape, "decayForgeConstant", 110),
            WordBase = I(Required(shape, "wordCount"), "base", 3),
            WordScale = D(Required(shape, "wordCount"), "scale", 9),
            BankBase = I(Required(shape, "bankSize"), "base", 3),
            BankScale = D(Required(shape, "bankSize"), "scale", 3),
            BankExponent = D(Required(shape, "bankSize"), "exponent", 0.8),
            AvgLenBase = D(Required(shape, "avgWordLength"), "base", 3.3),
            AvgLenScale = D(Required(shape, "avgWordLength"), "scale", 2.5),
            RarityBase = D(Required(shape, "rarity"), "base", 0.02),
            RarityScale = D(Required(shape, "rarity"), "scale", 0.28),
            BreatherEveryN = I(diff, "breatherEveryNForge", 5),
            BreatherWordPenalty = I(diff, "breatherWordPenalty", 1),
            BreatherRarityMultiplier = D(diff, "breatherRarityMultiplier", 0.6)
        };
        cfg.Weights = new DifficultyWeights
        {
            PerExtraWord = D(w, "perExtraWord", 4),
            PerExtraBankLetter = D(w, "perExtraBankLetter", 5),
            PerExtraAvgLength = D(w, "perExtraAvgLength", 4.5),
            Rarity = D(w, "rarity", 18),
            BreatherBonus = D(diff, "breatherBonus", -8),
            MinDI = D(diff, "minDI", 5),
            MaxDI = D(diff, "maxDI", 95)
        };
        cfg.ParTime = new ParTimeTuning
        {
            Base = D(par, "base", 14),
            Linear = D(par, "linear", 1.15),
            Quadratic = D(par, "quadratic", 0.004),
            CapSeconds = D(par, "capSeconds", 120)
        };

        JsonElement contract = Required(diff, "contentContract");
        cfg.Content = new ContentContract
        {
            MinShortWordsLengthLTE4 = I(contract, "minShortWordsLengthLTE4", 2),
            KeystoneWords = I(contract, "keystoneWords", 1),
            MinGridIntersections = I(contract, "minGridIntersections", 2),
            MaxAbandonRate = D(contract, "maxAbandonRate", 0.08),
            MaxHintsPerForge = D(contract, "maxHintsPerForge", 1.2)
        };
        JsonElement anti = Required(diff, "antiFrustration");
        cfg.AntiFrustration = new AntiFrustrationConfig
        {
            FreeNudgeAfterSeconds = I(anti, "freeNudgeAfterSeconds", 45),
            FreeNudgePerWindow = I(anti, "freeNudgePerWindow", 1),
            FreeLetterRevealAfterWrongAttempts = I(anti, "freeLetterRevealAfterWrongAttempts", 3)
        };
        foreach (JsonElement p in Required(diff, "packs").EnumerateArray())
        {
            JsonElement band = Required(p, "band");
            cfg.PackBands.Add(new PackBand
            {
                FirstForge = I(p, "firstForge", 0),
                LastForge = I(p, "lastForge", 0),
                MinDI = band[0].GetDouble(),
                MaxDI = band[1].GetDouble(),
                Theme = Str(p, "theme", ""),
                TargetMedianSolveSeconds = D(p, "targetMedianSolveSeconds", 0)
            });
        }

        // ---- economy ----------------------------------------------------
        JsonElement coins = Required(econ, "coins");
        JsonElement flow = Required(coins, "flow");
        JsonElement starBonus = Required(coins, "starBonus");
        cfg.Economy = new EconomyTuning
        {
            Base = I(coins, "base", 20),
            PerSolutionWord = I(coins, "perSolutionWord", 6),
            PerBonusWord = I(coins, "perBonusWord", 3),
            BonusWordCap = I(coins, "bonusWordCap", 8),
            FlowStep = D(flow, "stepPerCleanFind", 0.15),
            FlowCap = D(flow, "capMultiplier", 2.5),
            HintCostBase = I(coins, "hintCostBase", 90),
            HintCostStep = I(coins, "hintCostStep", 60),
            MaxHintsPerForge = I(coins, "maxHintsPerForge", 3),
            StarBonus1 = I(starBonus, "1", 0),
            StarBonus2 = I(starBonus, "2", 15),
            StarBonus3 = I(starBonus, "3", 40)
        };
        JsonElement sinks = Required(econ, "sinks");
        JsonElement rewardedAd = Required(Required(marks, "sources"), "rewardedAd");
        cfg.Marks = new MarkConfig
        {
            CoinEquivalent = I(sinks, "markCoinEquivalent", 200),
            DailyCapFromAds = I(rewardedAd, "dailyCap", 6),
            PerRewardedAd = I(rewardedAd, "marks", 1),
            BonusMarkEveryNAd = I(rewardedAd, "bonusMarkEveryNAd", 3)
        };
        cfg.Sinks = new SinkConfig
        {
            RuneDraftRerollCoins = I(sinks, "runeDraftReroll", 200),
            CoolForgeCoins = I(sinks, "coolForge", 150),
            StreakFreezeCoins = I(sinks, "streakFreeze", 200)
        };

        // ---- heat -------------------------------------------------------
        cfg.Heat = new HeatTuning
        {
            GainBase = D(heat, "gainBase", 8),
            GainPerDI = D(heat, "gainPerDI", 0.20),
            GainPerParOverrun = D(heat, "gainPerParOverrun", 6),
            CoolOnPerfect = D(heat, "coolOnPerfectForge", 5),
            Max = D(heat, "max", 100),
            MaxForgesPerRun = I(heat, "maxForgesPerRun", 8)
        };
        cfg.DraftsAfterForges = heat.TryGetProperty("draftsAfterForges", out var draftForges)
            && draftForges.ValueKind == JsonValueKind.Array
                ? draftForges.EnumerateArray().Select(x => x.GetInt32()).ToArray()
                : new[] { 1, 3, 5 };
        cfg.CoolForgeHeatDelta = D(heat, "coolForgeReward", -25);

        // ---- ads --------------------------------------------------------
        JsonElement g = Required(ads, "global");
        // Remote config may set "interstitialKillSwitch": true to disable interstitials
        // globally. Absent or false => interstitials remain eligible (subject to caps).
        bool interstitialEnabled = !B(g, "interstitialKillSwitch", false);
        cfg.AdPolicy = new AdPolicyConfig
        {
            FirstInterstitialForge = I(g, "firstInterstitialForge", 12),
            FirstInterstitialSession = I(g, "firstInterstitialSession", 2),
            InterstitialEveryNForges = I(g, "interstitialEveryNForges", 4),
            InterstitialMinGapSeconds = (long)D(g, "interstitialMinGapSeconds", 120),
            InterstitialMaxPerDay = I(g, "interstitialMaxPerDay", 3),
            SuppressWithinSecondsOfRewarded = (long)D(g, "suppressInterstitialWithinSecondsOfRewarded", 90),
            RewardedSoftCapPerUserPerDay = I(g, "rewardedSoftCapPerUserPerDay", 8),
            AppOpenMinSessions = I(g, "appOpenMinSessions", 3),
            AppOpenMinGapSeconds = (long)D(g, "appOpenMinGapSeconds", 14400),
            AppOpenMaxPerDay = I(g, "appOpenMaxPerDay", 1),
            BannerEnabled = B(g, "bannerEnabled", false),
            InterstitialEnabled = interstitialEnabled
        };

        var rules = new Dictionary<string, AdPlacementRule>(StringComparer.Ordinal);
        foreach (JsonElement pl in Required(ads, "placements").EnumerateArray())
        {
            string id = Str(pl, "id", "");
            if (id.Length == 0) { cfg._diagnostics.Add("placement with empty id skipped"); continue; }
            string unit = Str(pl, "unit", "rewarded");
            rules[id] = new AdPlacementRule
            {
                Id = id,
                Unit = unit switch
                {
                    "interstitial" => AdUnit.Interstitial,
                    "app_open" => AdUnit.AppOpen,
                    "banner" => AdUnit.Banner,
                    _ => AdUnit.Rewarded
                },
                MinLifetimeForge = I(pl, "minForge", 0),
                MinSession = 1,
                CooldownSeconds = (long)D(pl, "cooldownSeconds", 0),
                RequiresOptIn = unit == "rewarded",
                VisibleAdLabel = true,
                Enabled = unit != "banner"
            };
        }
        // Merge the per-placement caps from the compiled default table; the CSV's
        // "cap" column is free text, so the authoritative numeric caps live here.
        foreach (var (id, rule) in DefaultPlacements.Build())
        {
            if (!rules.TryGetValue(id, out var parsed)) continue;
            rules[id] = new AdPlacementRule
            {
                Id = parsed.Id, Unit = parsed.Unit,
                MinLifetimeForge = parsed.MinLifetimeForge,
                MinSession = parsed.MinSession,
                CooldownSeconds = parsed.CooldownSeconds,
                DailyCap = rule.DailyCap,
                RequiresOptIn = parsed.RequiresOptIn,
                VisibleAdLabel = parsed.VisibleAdLabel,
                Enabled = parsed.Enabled
            };
        }
        cfg.Placements = rules;

        // ---- runes ------------------------------------------------------
        foreach (JsonElement rune in Required(r, "runes").EnumerateArray())
        {
            cfg.Runes.Add(new RuneData
            {
                Id = Str(rune, "id", ""),
                Name = Str(rune, "name", ""),
                Kind = ParseKind(Str(rune, "type", "score")),
                Rarity = ParseTier(Str(rune, "rarity", "common")),
                CoinDeltaPct = D(rune, "coinDeltaPct", 0),
                DiDelta = D(rune, "diDelta", 0),
                Effect = Str(rune, "effect", "")
            });
        }
        JsonElement draft = Required(r, "runeDraft");
        JsonElement weights = Required(draft, "rarityWeights");
        cfg.Draft = new RuneDraftConfig
        {
            OptionsPerDraft = I(draft, "optionsPerDraft", 3),
            CommonWeight = D(weights, "common", 0.60),
            UncommonWeight = D(weights, "uncommon", 0.30),
            RareWeight = D(weights, "rare", 0.10),
            GuaranteeNonScoreOption = B(draft, "guaranteeNonScoreOption", true),
            MaxBuildCoinDeltaPct = D(draft, "maxBuildCoinDeltaPct", 0.30),
            MaxSingleRuneCoinDeltaPct = D(draft, "maxSingleRuneCoinDeltaPct", 0.10),
            TutorialFirstDraft = draft.TryGetProperty("tutorialFirstDraft", out var tut)
                ? tut.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                : Array.Empty<string>()
        };

        // ---- progression ------------------------------------------------
        cfg.Progression = new ProgressionConfig
        {
            ForgesPerPack = I(prog, "forgesPerPack", 20),
            PacksPerChapter = I(prog, "packsPerChapter", 5),
            LaunchForgeCount = I(prog, "launchForgeCount", 400),
            AtlasScenesPerChapter = I(prog, "atlasScenesPerChapter", 20),
            StarsToUnlockAtlasPerPack = prog.TryGetProperty("starsToUnlockAtlasPerPack", out var stars)
                ? stars.EnumerateArray().Select(x => x.GetInt32()).ToArray() : new[] { 40, 60 },
            DailyForgeSeedEpoch = DateTime.TryParse(Str(prog, "dailyForgeSeedEpoch", "2026-01-01"),
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var epoch) ? epoch : new DateTime(2026, 1, 1),
            WeeklyGildedForgeCount = I(prog, "weeklyGildedForgeCount", 7),
            FreeStreakFreezePerWeek = I(prog, "freeStreakFreezePerWeek", 1),
            FirstSession = prog.TryGetProperty("firstSession", out var fs)
                ? new FirstSessionConfig
                {
                    Forges = I(fs, "forges", 5),
                    Interstitials = I(fs, "interstitials", 0),
                    ScriptedFreeHintOnForge = I(fs, "scriptedFreeHintOnForge", 4),
                    RewardedEntryPointsVisibleFromForge = I(fs, "rewardedEntryPointsVisibleFromForge", 3)
                }
                : new FirstSessionConfig()
        };

        cfg.Validate();
        return cfg;
    }

    /// <summary>
    /// The remote-config compliance lever. Kept as an explicit method (not a
    /// public setter) so disabling interstitials is always an auditable action
    /// and never an accidental property assignment. Rewarded ads are untouched:
    /// Forge Pass / kill switch remove interruption, not player opt-in value.
    /// </summary>
    public void SetInterstitialEnabled(bool enabled)
    {
        var c = AdPolicy;
        AdPolicy = new AdPolicyConfig
        {
            FirstInterstitialForge = c.FirstInterstitialForge,
            FirstInterstitialSession = c.FirstInterstitialSession,
            InterstitialEveryNForges = c.InterstitialEveryNForges,
            InterstitialMinGapSeconds = c.InterstitialMinGapSeconds,
            InterstitialMaxPerDay = c.InterstitialMaxPerDay,
            SuppressWithinSecondsOfRewarded = c.SuppressWithinSecondsOfRewarded,
            RewardedSoftCapPerUserPerDay = c.RewardedSoftCapPerUserPerDay,
            AppOpenMinSessions = c.AppOpenMinSessions,
            AppOpenMinGapSeconds = c.AppOpenMinGapSeconds,
            AppOpenMaxPerDay = c.AppOpenMaxPerDay,
            BannerEnabled = c.BannerEnabled,
            InterstitialEnabled = enabled
        };
    }

    private void Validate()
    {
        if (Economy.FlowCap < 1.0) Warn("flow.capMultiplier (using 2.5 in session maths)");
        if (Economy.FlowStep <= 0) Warn("flow.stepPerCleanFind");
        if (Economy.MaxHintsPerForge <= 0) Warn("maxHintsPerForge");
        if (Heat.MaxForgesPerRun <= 0) Warn("heat.maxForgesPerRun");
        if (Runes.Count == 0) Warn("runes (empty)");
        foreach (var rune in Runes)
        {
            if (Math.Abs(rune.CoinDeltaPct) > Draft.MaxSingleRuneCoinDeltaPct + 0.0001)
                Warn($"rune '{rune.Id}' exceeds maxSingleRuneCoinDeltaPct");
        }
        if (PackBands.Count == 0) Warn("difficulty.packs (empty)");
    }

    private void Warn(string key) => _diagnostics.Add($"invalid/missing '{key}' -> compiled default");

    public PackBand? BandFor(int forgeId)
        => PackBands.FirstOrDefault(b => forgeId >= b.FirstForge && forgeId <= b.LastForge);

    // ---- tolerant readers (mirrors the balance simulator's ParseConfig) ----
    private static JsonElement Required(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v)
            ? v : throw new InvalidDataException($"balance_config.json: missing '{name}'");

    private static double D(JsonElement e, string name, double fallback)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && double.IsFinite(v.GetDouble()) ? v.GetDouble() : fallback;

    private static int I(JsonElement e, string name, int fallback)
        => (int)Math.Round(D(e, name, fallback));

    private static bool B(JsonElement e, string name, bool fallback)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : fallback;

    private static string Str(JsonElement e, string name, string fallback)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    private static RuneKind ParseKind(string s) => s switch
    {
        "utility" => RuneKind.Utility,
        "economy" => RuneKind.Economy,
        _ => RuneKind.Score
    };

    private static RuneTier ParseTier(string s) => s switch
    {
        "uncommon" => RuneTier.Uncommon,
        "rare" => RuneTier.Rare,
        _ => RuneTier.Common
    };
}
