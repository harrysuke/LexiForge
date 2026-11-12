using System.Globalization;
using System.Text;
using System.Text.Json;
using LexiForge.Ads;
using LexiForge.Core;

namespace LexiForge.Sim;

/// <summary>
/// Headless Monte Carlo balance simulator.
///
///   dotnet run --project godot/tools/BalanceSim -- [path/to/balance_config.json] [players] [days]
///
/// It links the EXACT same pure-C# formulas the Godot game ships (godot/core,
/// godot/ads/AdTypes.cs) and reads the single-source-of-truth JSON, so a tuning
/// change can be validated before it ever reaches a build.
///
/// HONESTY NOTE: this projects economy and ad-load BEHAVIOUR only. Retention
/// (D1/D7/D30) cannot be simulated from first principles — measure it in soft launch.
/// </summary>
internal static class Program
{
    private const int DefaultPlayers = 10_000;
    private const int DefaultDays = 30;

    private static int Main(string[] args)
    {
        string configPath = args.Length > 0 ? args[0] : LocateConfig();
        int players = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : DefaultPlayers;
        int days = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : DefaultDays;

        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        JsonElement root = doc.RootElement;

        var cfg = ParseConfig(root);
        var rng = new Random(20_261_111);
        var agg = new Aggregate();

        Console.WriteLine($"LexiForge balance simulator | config={cfg.Version} | players={players:N0} | days={days} | seed=20261111");
        Console.WriteLine(new string('=', 96));

        PrintDifficultyCurve(cfg);

        for (int p = 0; p < players; p++)
            SimulatePlayer(cfg, rng, days, agg);

        PrintResults(cfg, agg, players, days);
        WriteDifficultyCsv(cfg, configPath);
        return 0;
    }

    // ------------------------------------------------------------------ config

    private sealed record Rune(string Id, string Rarity, double CoinDelta, double DiDelta);

    private sealed class SimConfig
    {
        public string Version = "";
        public DifficultyShapeTuning Shape = new();
        public DifficultyWeights Weights = new();
        public ParTimeTuning ParTime = new();
        public EconomyTuning Economy = new();
        public HeatTuning Heat = new();
        public AdPolicyConfig AdPolicy = new();
        public Dictionary<string, AdPlacementRule> Rules = new();
        public List<Rune> Runes = new();
        public double MaxBuildDelta;
        public double RewardedEcpm, InterstitialEcpm, AppOpenEcpm, IapConversion, IapPriceUsd;
        public double SessionsPerDay, ForgesPerSession;
        public double TargetHintsPerForge, TargetRewardedOptIn, RewardedPerEngagedTarget;
        public int AtlasBase, AtlasStep, AtlasMax, MarkCoinEquivalent;
    }

    private static SimConfig ParseConfig(JsonElement r)
    {
        JsonElement diff = r.GetProperty("difficulty");
        JsonElement shape = diff.GetProperty("shape");
        JsonElement w = diff.GetProperty("weights");
        JsonElement par = diff.GetProperty("parTime");
        JsonElement econ = r.GetProperty("economy");
        JsonElement coins = econ.GetProperty("coins");
        JsonElement flow = coins.GetProperty("flow");
        JsonElement heat = r.GetProperty("heat");
        JsonElement ads = r.GetProperty("ads");
        JsonElement g = ads.GetProperty("global");
        JsonElement rev = ads.GetProperty("revenueAssumptions");
        JsonElement kpis = r.GetProperty("kpis");
        JsonElement sinks = econ.GetProperty("sinks");
        JsonElement atlas = sinks.GetProperty("atlasScene");
        JsonElement draft = r.GetProperty("runeDraft");

        var c = new SimConfig
        {
            Version = r.GetProperty("configVersion").GetString() ?? "?",
            Shape = new DifficultyShapeTuning
            {
                DecayForgeConstant = D(shape, "decayForgeConstant", 110),
                WordBase = I(shape.GetProperty("wordCount"), "base", 3),
                WordScale = D(shape.GetProperty("wordCount"), "scale", 9),
                BankBase = I(shape.GetProperty("bankSize"), "base", 3),
                BankScale = D(shape.GetProperty("bankSize"), "scale", 3),
                BankExponent = D(shape.GetProperty("bankSize"), "exponent", 0.8),
                AvgLenBase = D(shape.GetProperty("avgWordLength"), "base", 3.3),
                AvgLenScale = D(shape.GetProperty("avgWordLength"), "scale", 2.5),
                RarityBase = D(shape.GetProperty("rarity"), "base", 0.02),
                RarityScale = D(shape.GetProperty("rarity"), "scale", 0.28),
                BreatherEveryN = I(diff, "breatherEveryNForge", 5),
                BreatherOffset = I(diff, "breatherOffset", 5),
                BreatherWordPenalty = I(diff, "breatherWordPenalty", 1),
                BreatherRarityMultiplier = D(diff, "breatherRarityMultiplier", 0.6)
            },
            Weights = new DifficultyWeights
            {
                PerExtraWord = D(w, "perExtraWord", 4),
                PerExtraBankLetter = D(w, "perExtraBankLetter", 5),
                PerExtraAvgLength = D(w, "perExtraAvgLength", 4.5),
                Rarity = D(w, "rarity", 18),
                BreatherBonus = D(diff, "breatherBonus", -8),
                MinDI = D(diff, "minDI", 5),
                MaxDI = D(diff, "maxDI", 95)
            },
            ParTime = new ParTimeTuning
            {
                Base = D(par, "base", 14),
                Linear = D(par, "linear", 1.15),
                Quadratic = D(par, "quadratic", 0.004),
                CapSeconds = D(par, "capSeconds", 120)
            },
            Economy = new EconomyTuning
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
                StarBonus2 = I(coins.GetProperty("starBonus"), "2", 15),
                StarBonus3 = I(coins.GetProperty("starBonus"), "3", 40)
            },
            Heat = new HeatTuning
            {
                GainBase = D(heat, "gainBase", 8),
                GainPerDI = D(heat, "gainPerDI", 0.20),
                GainPerParOverrun = D(heat, "gainPerParOverrun", 6),
                CoolOnPerfect = D(heat, "coolOnPerfectForge", 5),
                Max = D(heat, "max", 100),
                MaxForgesPerRun = I(heat, "maxForgesPerRun", 8)
            },
            AdPolicy = new AdPolicyConfig
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
                BannerEnabled = B(g, "bannerEnabled", false)
            },
            MaxBuildDelta = D(draft, "maxBuildCoinDeltaPct", 0.30),
            RewardedEcpm = D(rev, "rewardedEcpmUsd", 12),
            InterstitialEcpm = D(rev, "interstitialEcpmUsd", 6),
            AppOpenEcpm = D(rev, "appOpenEcpmUsd", 3.5),
            IapConversion = D(rev, "forgePassConversion", 0.02),
            IapPriceUsd = D(rev, "forgePassPriceUsd", 4.99),
            SessionsPerDay = D(kpis, "sessionsPerDay", 2.6),
            ForgesPerSession = D(kpis, "medianForgesPerSession", 3.6),
            TargetHintsPerForge = D(kpis, "hintsPerForge", 0.6),
            TargetRewardedOptIn = D(rev, "rewardedOptInRate", 0.30),
            RewardedPerEngagedTarget = D(rev, "rewardedImpPerEngagedUserPerDay", 3.2),
            AtlasBase = I(atlas, "base", 400),
            AtlasStep = I(atlas, "step", 100),
            AtlasMax = I(atlas, "max", 1200),
            MarkCoinEquivalent = I(sinks, "markCoinEquivalent", 200)
        };

        foreach (JsonElement rune in r.GetProperty("runes").EnumerateArray())
            c.Runes.Add(new Rune(
                rune.GetProperty("id").GetString() ?? "",
                rune.GetProperty("rarity").GetString() ?? "common",
                D(rune, "coinDeltaPct", 0),
                D(rune, "diDelta", 0)));

        foreach (JsonElement pl in ads.GetProperty("placements").EnumerateArray())
        {
            string id = pl.GetProperty("id").GetString() ?? "";
            string unit = pl.GetProperty("unit").GetString() ?? "rewarded";
            c.Rules[id] = new AdPlacementRule
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
                DailyCap = 0,
                RequiresOptIn = unit == "rewarded",
                VisibleAdLabel = true,
                Enabled = unit != "banner"
            };
        }
        return c;
    }

    private static double D(JsonElement e, string name, double fallback)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : fallback;
    private static int I(JsonElement e, string name, int fallback)
        => (int)Math.Round(D(e, name, fallback));
    private static bool B(JsonElement e, string name, bool fallback)
        => e.TryGetProperty(name, out var v) ? v.GetBoolean() : fallback;

    // ------------------------------------------------------------------ simulation

    private sealed class Aggregate
    {
        public long Forges, Hints, HintAds, DoubleAds, MarkAds, CoolAds, FreezeGiftAds;
        public long InterstitialImps, AppOpenImps;
        public long CoinsEarned, CoinsSpent, MarksEarned, MarksSpent;
        public double SolveSeconds, RatioSum, HeatSum, BuildSum;
        public long Runs, ForgesInRuns, PerfectForges, StarsSum, Abandoned;
        public readonly Dictionary<string, long> Denials = new();
        public void Deny(string reason) => Denials[reason] = Denials.GetValueOrDefault(reason) + 1;
    }

    private static void SimulatePlayer(SimConfig c, Random rng, int days, Aggregate a)
    {
        double skill = Clamp(Gauss(rng, 0.5, 0.18), 0.05, 0.98);
        // Player-level segment: only rewardedOptInRate of DAU ever opts into ads.
        double adAffinity = rng.NextDouble() < c.TargetRewardedOptIn
            ? Math.Pow(rng.NextDouble(), 0.8)
            : 0.0;
        bool forgePass = rng.NextDouble() < 0.02;
        int lifetimeForges = 0, sessions = 0;
        int coins = 300, marks = 0, atlasIndex = 0;
        long now = 1_800_000_000;

        var state = new AdPolicyState { IsForgePassOwner = forgePass };
        double heat = 0, buildCoin = 0, buildDi = 0;
        int forgesInRun = 0, runs = 0, marksToday = 0;

        // Every rewarded ad, on EVERY placement, mints 1 Sponsor Mark.
        // Cap 6/day; the 3rd ad of the day mints one bonus Mark.
        void MintRewarded(string kind)
        {
            switch (kind)
            {
                case "hint": a.HintAds++; break;
                case "double": a.DoubleAds++; break;
                case "mark": a.MarkAds++; break;
                case "cool": a.CoolAds++; break;
                default: a.FreezeGiftAds++; break;
            }
            AdPolicy.RecordRewarded(state, now);
            if (marksToday >= 6) return;
            marks++; a.MarksEarned++; marksToday++;
            if (state.RewardedToday == 3 && marksToday < 6) { marks++; a.MarksEarned++; marksToday++; }
        }

        for (int day = 1; day <= days; day++)
        {
            AdPolicy.ResetDaily(state);
            marksToday = 0;
            int daySessions = Clamp((int)Math.Round(Gauss(rng, c.SessionsPerDay, 0.9)), 1, 6);

            for (int s = 0; s < daySessions; s++)
            {
                sessions++;
                state.Sessions = sessions;
                now += 4 * 3600;

                if (AdPolicy.CanShowAppOpen(state, c.AdPolicy, now, out _))
                {
                    AdPolicy.RecordAppOpen(state, now);
                    a.AppOpenImps++;
                }

                int sessionForges = Clamp((int)Math.Round(Gauss(rng, c.ForgesPerSession, 1.2)), 1, 8);

                for (int f = 0; f < sessionForges; f++)
                {
                    lifetimeForges++;
                    state.LifetimeForges = lifetimeForges;
                    state.ForgesSinceLastInterstitial++;
                    state.InRun = true;
                    forgesInRun++;

                    LevelShape shape = DifficultyModel.ShapeFor(lifetimeForges, c.Shape);
                    double di = Math.Max(c.Weights.MinDI, DifficultyModel.Index(shape, c.Weights) + buildDi);
                    double par = DifficultyModel.ParTimeSeconds(di, c.ParTime);

                    // Draft after forges 1, 3, 5 of a run (score-chasing choice heuristic).
                    if (forgesInRun is 1 or 3 or 5)
                    {
                        var pick = DraftRune(c, rng);
                        buildCoin = Math.Min(c.MaxBuildDelta, buildCoin + pick.CoinDelta);
                        buildDi = Math.Max(-12, buildDi + pick.DiDelta);
                    }

                    double ratio = Clamp(0.75 + 0.55 * (1 - skill) + 0.13 * Gauss(rng, 0, 1)
                                         - 0.02 * Math.Max(0, -buildDi), 0.60, 2.20);
                    double solve = par * ratio;
                    now += (long)Math.Round(solve + 18);

                    int wrong = Math.Min(6, Poisson(rng, 0.35 + 0.018 * di * (1 - skill)));
                    int cleanStreak = Math.Max(0, shape.WordCount - wrong);
                    double flowPeak = EconomyModel.FlowFor(cleanStreak, c.Economy);
                    int bonusWords = Math.Min(c.Economy.BonusWordCap, Poisson(rng, 1.0 + 2.0 * skill));

                    double hintP = Clamp(0.06 + 0.006 * di + 0.30 * (1 - skill) - 0.015 * Math.Max(0, -buildDi), 0, 0.80);
                    int hints = rng.NextDouble() < hintP ? 1 + (rng.NextDouble() < hintP * 0.35 ? 1 : 0) : 0;
                    hints = Math.Min(hints, c.Economy.MaxHintsPerForge);
                    a.Hints += hints;

                    int hintAdsThisForge = 0;
                    for (int h = 1; h <= hints; h++)
                    {
                        bool canAd = hintAdsThisForge < 2
                            && state.RewardedToday < c.AdPolicy.RewardedSoftCapPerUserPerDay
                            && rng.NextDouble() < adAffinity * 0.60;
                        if (canAd)
                        {
                            hintAdsThisForge++;
                            MintRewarded("hint");
                        }
                        else
                        {
                            int cost = EconomyModel.HintCost(h, c.Economy);
                            if (coins >= cost) { coins -= cost; a.CoinsSpent += cost; }
                            // else: free nudge — never a hard block
                        }
                    }

                    int stars = EconomyModel.Stars(hints, wrong, solve, par, c.Economy);
                    a.StarsSum += stars;
                    if (stars == 3) a.PerfectForges++;
                    bool perfect = stars == 3;

                    int earned = EconomyModel.LevelCoins(shape.WordCount, bonusWords, flowPeak, buildCoin, c.Economy)
                               + EconomyModel.StarBonus(stars, c.Economy);

                    // R_DOUBLE: opt-in post-results doubling.
                    if (state.RewardedToday < c.AdPolicy.RewardedSoftCapPerUserPerDay
                        && rng.NextDouble() < adAffinity * 0.22)
                    {
                        earned *= 2;
                        MintRewarded("double");
                    }

                    coins += earned;
                    a.CoinsEarned += earned;
                    a.SolveSeconds += solve;
                    a.RatioSum += ratio;
                    a.Forges++;
                    double quitP = 0.015 * (1 + di / 50.0) + (ratio > 2.0 ? 0.20 : 0.0);
                    bool abandoned = rng.NextDouble() < quitP;
                    if (abandoned) a.Abandoned++;
                    state.ForgeWasAbandoned = abandoned;

                    heat = HeatModel.Next(heat, di, ratio, perfect, c.Heat);
                    a.HeatSum += heat;

                    // R_MARK: diegetic Sponsor Mark, only from the Patron Board / run end.
                    bool runEnds = heat >= c.Heat.Max || forgesInRun >= c.Heat.MaxForgesPerRun;
                    if (runEnds)
                    {
                        runs++;
                        a.Runs++;
                        a.ForgesInRuns += forgesInRun;
                        a.BuildSum += buildCoin;
                        state.InRun = false;

                        // Patron Board sinks: draft reroll, weekly Gilded Forge, cosmetics.
                        if (marks >= 1 && rng.NextDouble() < 0.28) { marks--; a.MarksSpent++; }
                        if (marks >= 3 && day % 7 == 0 && rng.NextDouble() < 0.55) { marks -= 3; a.MarksSpent += 3; }
                        if (marks >= 8 && rng.NextDouble() < 0.06) { marks -= 8; a.MarksSpent += 8; }

                        // R_MARK — explicit "receive a patron's message" entry point.
                        if (state.RewardedToday < c.AdPolicy.RewardedSoftCapPerUserPerDay
                            && marksToday < 6 && rng.NextDouble() < adAffinity * 0.20)
                            MintRewarded("mark");

                        // R_COOL — two free alternatives exist, so this is never a paywall.
                        bool cooled = false;
                        if (state.RewardedToday < c.AdPolicy.RewardedSoftCapPerUserPerDay
                            && rng.NextDouble() < adAffinity * 0.10)
                        {
                            MintRewarded("cool");
                            cooled = true;
                        }
                        else if (rng.NextDouble() < 0.12 && marks > 0) { marks--; a.MarksSpent++; cooled = true; }
                        else if (rng.NextDouble() < 0.10 && coins >= 150) { coins -= 150; a.CoinsSpent += 150; cooled = true; }

                        if (AdPolicy.CanShowInterstitial(state, c.AdPolicy, now, out string why))
                        {
                            a.InterstitialImps++;
                            AdPolicy.RecordInterstitial(state, now);
                        }
                        else a.Deny(why);

                        heat = cooled ? Math.Max(0, heat - 25) : 0;
                        forgesInRun = 0;
                        buildCoin = 0;
                        buildDi = 0;
                    }
                }

                // Occasional reward-free rewarded surfaces (gift / freeze / refill).
                if (day > 1 && state.RewardedToday < c.AdPolicy.RewardedSoftCapPerUserPerDay
                    && rng.NextDouble() < adAffinity * 0.18)
                {
                    MintRewarded("gift");
                    coins += 180; a.CoinsEarned += 180;
                }
            }

            // Atlas sink: cosmetic, no gameplay power. Keeps coin inflation honest.
            int purchased = 0;
            while (coins > 900 && purchased < 3)
            {
                int cost = Math.Min(c.AtlasMax, c.AtlasBase + c.AtlasStep * (atlasIndex % 9));
                if (coins < cost) break;
                coins -= cost; a.CoinsSpent += cost;
                atlasIndex++; purchased++;
            }
            now += 8 * 3600;
        }
    }

    private static Rune DraftRune(SimConfig c, Random rng)
    {
        // Offer 3, guarantee at least one non-score option (matches design rule).
        var offer = new List<Rune>();
        for (int i = 0; i < 3; i++)
        {
            double roll = rng.NextDouble();
            string rarity = roll < 0.60 ? "common" : roll < 0.90 ? "uncommon" : "rare";
            var pool = c.Runes.Where(x => x.Rarity == rarity).ToList();
            offer.Add(pool[rng.Next(pool.Count)]);
        }
        if (offer.All(x => x.CoinDelta > 0))
        {
            offer[2] = c.Runes.First(x => x.CoinDelta <= 0);
        }
        // Score-chasing player picks the highest coin delta ~70% of the time.
        if (rng.NextDouble() < 0.70) return offer.OrderByDescending(x => x.CoinDelta).First();
        return offer[rng.Next(offer.Count)];
    }

    // ------------------------------------------------------------------ output

    private static void PrintDifficultyCurve(SimConfig c)
    {
        Console.WriteLine("Difficulty curve (flow = 1.6x reference)");
        Console.WriteLine("   Forge  Words   Bank   AvgLen   Rarity       DI      Par    Coins");
        // Non-breather indices: every 5th forge is intentionally a relief level.
        foreach (int idx in new[] { 1, 12, 22, 52, 102, 202, 302, 402 })
        {
            var s = DifficultyModel.ShapeFor(idx, c.Shape);
            double di = DifficultyModel.Index(s, c.Weights);
            double par = DifficultyModel.ParTimeSeconds(di, c.ParTime);
            int coins = EconomyModel.LevelCoins(s.WordCount, 0, 1.6, 0, c.Economy);
            Console.WriteLine($"{idx,8} {s.WordCount,6} {s.BankSize,5} {s.AverageWordLength,8:F2} {s.Rarity,8:F2} {di,7:F1} {par,8:F1} {coins,7}");
        }
        Console.WriteLine(new string('-', 96));
    }

    private static void PrintResults(SimConfig c, Aggregate a, int players, int days)
    {
        double pd = (double)players * days;
        double rewarded = a.HintAds + a.DoubleAds + a.MarkAds + a.CoolAds + a.FreezeGiftAds;
        double arpdau = (a.HintAds + a.DoubleAds + a.MarkAds + a.CoolAds + a.FreezeGiftAds) / pd * c.RewardedEcpm / 1000.0
                      + a.InterstitialImps / pd * c.InterstitialEcpm / 1000.0
                      + a.AppOpenImps / pd * c.AppOpenEcpm / 1000.0
                      + c.IapConversion * c.IapPriceUsd / 30.0;

        Console.WriteLine("Projected behaviour (per user per day, unless noted)");
        Console.WriteLine(new string('-', 96));
        Row("Forges", a.Forges / pd, "~9.4 (2.6 sessions x 3.6 forges)", "F2");
        Row("Solve time (s)", a.SolveSeconds / a.Forges, "vs par", "F1");
        Row("Solve / par", a.RatioSum / a.Forges, "0.9 - 1.3 target", "F2");
        Row("Abandon rate", a.Abandoned / a.Forges, "< 0.08 target", "P2");
        Row("Hints / forge", a.Hints / (double)a.Forges, c.TargetHintsPerForge + " target", "F2");
        Row("Avg stars", a.StarsSum / (double)a.Forges, "1 - 3", "F2");
        Row("Coins earned", a.CoinsEarned / pd, "9.4 forges/day x ~200 coins", "F0");
        Row("Coins spent", a.CoinsSpent / pd, "hints + Atlas", "F0");
        Row("Net coins", (a.CoinsEarned - a.CoinsSpent) / pd, "watch for inflation", "F0");
        Row("Rewarded ads", rewarded / pd, "per DAU", "F2");
        Row("Rewarded / engaged user", rewarded / (pd * c.TargetRewardedOptIn), c.RewardedPerEngagedTarget + " target", "F2");
        Row("  hint / double / mark / cool / other", 0, "", "F0");
        Console.WriteLine($"    hint {a.HintAds / pd:F2} | double {a.DoubleAds / pd:F2} | mark {a.MarkAds / pd:F2} | cool {a.CoolAds / pd:F2} | freeze-gift-refill {a.FreezeGiftAds / pd:F2}");
        Row("Interstitial ads", a.InterstitialImps / pd, "<= 2.8 cap", "F2");
        Row("App-open ads", a.AppOpenImps / pd, "<= 1.0 cap", "F2");
        Row("Marks earned", a.MarksEarned / pd, "from rewarded + free", "F2");
        Row("Marks spent", a.MarksSpent / pd, "sinks active", "F2");
        Row("Run length (forges)", a.ForgesInRuns / (double)Math.Max(1, a.Runs), "5 - 7 design", "F2");
        Row("Avg rune build delta", a.BuildSum / Math.Max(1, a.Runs), "<= 0.30 cap", "P2");
        Row("Projected ARPDAU (USD)", arpdau, "$0.020 - 0.040 base target", "F4");
        Console.WriteLine(new string('-', 96));

        Console.WriteLine("Interstitial denial reasons (validates suppression rules)");
        foreach (var (reason, count) in a.Denials.OrderByDescending(x => x.Value))
            Console.WriteLine($"  {reason,-24} {count,10:N0}");
        Console.WriteLine(new string('-', 96));
        Console.WriteLine("Retention (D1/D7/D30) is deliberately NOT simulated — measure in soft launch.");
    }

    private static void Row(string label, double value, string note, string fmt)
    {
        string text = fmt == "P2" ? value.ToString("P2", CultureInfo.InvariantCulture) : value.ToString(fmt, CultureInfo.InvariantCulture);
        Console.WriteLine($"  {label,-42} {text,12}   {note}");
    }

    private static void WriteDifficultyCsv(SimConfig c, string configPath)
    {
        string? designDir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        if (designDir is null) return;
        string outPath = Path.Combine(designDir, "difficulty_curve.csv");

        var sb = new StringBuilder();
        sb.AppendLine("forge,words,bank,avg_word_len,rarity,difficulty_index,par_seconds,coins_at_flow_1_6");
        for (int i = 1; i <= 400; i++)
        {
            var s = DifficultyModel.ShapeFor(i, c.Shape);
            double di = DifficultyModel.Index(s, c.Weights);
            sb.AppendLine(string.Join(',',
                i, s.WordCount, s.BankSize,
                s.AverageWordLength.ToString("F3", CultureInfo.InvariantCulture),
                s.Rarity.ToString("F3", CultureInfo.InvariantCulture),
                di.ToString("F2", CultureInfo.InvariantCulture),
                DifficultyModel.ParTimeSeconds(di, c.ParTime).ToString("F1", CultureInfo.InvariantCulture),
                EconomyModel.LevelCoins(s.WordCount, 0, 1.6, 0, c.Economy)));
        }
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"Wrote 400-forge curve -> {outPath}");
    }

    // ------------------------------------------------------------------ helpers

    private static string LocateConfig()
    {
        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (string root in roots)
        {
            var dir = new DirectoryInfo(root);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "design", "balance_config.json");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "balance_config.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("Cannot locate design/balance_config.json — pass it as the first argument.");
    }

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

    private static double Gauss(Random r, double mean, double sd)
    {
        double u1 = Math.Max(1e-9, r.NextDouble());
        double u2 = r.NextDouble();
        return mean + sd * Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    private static int Poisson(Random r, double lambda)
    {
        if (lambda <= 0) return 0;
        double limit = Math.Exp(-lambda), product = r.NextDouble();
        int k = 0;
        while (product > limit && k < 30) { k++; product *= r.NextDouble(); }
        return k;
    }
}
