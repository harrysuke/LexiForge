using LexiForge.Ads;
using LexiForge.Core;

namespace LexiForge.Tests;

/// <summary>
/// Headless verification harness for the Godot-free gameplay core.
/// Run: dotnet run --project godot/tools/CoreTests -- [repoRoot]
/// On Windows with App Control / WDAC (which blocks unsigned local apphost .exe):
///   dotnet build godot/tools/CoreTests/CoreTests.csproj
///   dotnet exec godot/tools/CoreTests/bin/Debug/net10.0/lexiforge-coretests.dll [repoRoot]
/// Fails with a non-zero exit code on the first failed assertion.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static readonly List<string> Failures = new();

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : LocateRoot();
        string configPath = Path.Combine(root, "design", "balance_config.json");
        string dataDir = Path.Combine(root, "godot", "data");

        Console.WriteLine($"LexiForge core verification | root={root}");
        Console.WriteLine(new string('=', 88));

        BalanceConfig cfg = BalanceConfig.Load(configPath);
        Check("config loads", cfg.ConfigVersion.Length > 0, cfg.ConfigVersion);
        Check("config has 12 runes", cfg.Runes.Count == 12, cfg.Runes.Count.ToString());
        Check("config has placements", cfg.Placements.Count >= 10, cfg.Placements.Count.ToString());
        Check("config has pack bands", cfg.PackBands.Count == 6, cfg.PackBands.Count.ToString());
        Check("draft forges 1/3/5", cfg.DraftsAfterForges.SequenceEqual(new[] { 1, 3, 5 }));
        Check("diagnostics empty", cfg.Diagnostics.Count == 0, string.Join(",", cfg.Diagnostics));
        Check("coin alternatives parsed from economy.sinks",
            cfg.Sinks.CoolForgeCoins == 150 && cfg.Sinks.RuneDraftRerollCoins == 200 &&
            cfg.Sinks.StreakFreezeCoins == 200,
            $"cool={cfg.Sinks.CoolForgeCoins} reroll={cfg.Sinks.RuneDraftRerollCoins} freeze={cfg.Sinks.StreakFreezeCoins}");

        TestDifficultyParity(cfg);
        TestWordTrie();
        TestAdPolicy(cfg);
        TestMarkGrant(cfg);
        TestRuneDraft(cfg);
        TestSaveData();
        TestForgeSession(cfg);
        TestSolutionWordsAreAuthoritative(cfg);
        TestRevealedKeystoneAfterDuplicateBonus(cfg);
        TestEchoRune(cfg);
        TestRunState(cfg);
        TestP0UiContracts(cfg);
        TestGeneratedForges(cfg, dataDir);
        TestAudioCatalog(root);

        Console.WriteLine(new string('-', 88));
        Console.WriteLine($"PASS {_passed}   FAIL {Failures.Count}");
        foreach (string f in Failures) Console.WriteLine($"  x {f}");
        return Failures.Count == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ tests
    private static void TestDifficultyParity(BalanceConfig cfg)
    {
        // The simulator's canonical curve table (GDD 6.1) must still hold.
        var shape = DifficultyModel.ShapeFor(1, cfg.Shape);
        Check("forge 1 wordCount", shape.WordCount == 3, shape.WordCount.ToString());
        Check("forge 1 bankSize", shape.BankSize == 3, shape.BankSize.ToString());
        double di = DifficultyModel.Index(shape, cfg.Weights);
        Check("forge 1 DI ~5.0", Math.Abs(di - 5.0) < 0.15, di.ToString("F2"));
        double par = DifficultyModel.ParTimeSeconds(di, cfg.ParTime);
        Check("forge 1 par ~20s", Math.Abs(par - 20.0) < 1.0, par.ToString("F2"));
        Check("flow cap 2.5", Math.Abs(EconomyModel.FlowFor(99, cfg.Economy) - 2.5) < 0.001);
        Check("hint costs 90/150/210",
            EconomyModel.HintCost(1, cfg.Economy) == 90 &&
            EconomyModel.HintCost(2, cfg.Economy) == 150 &&
            EconomyModel.HintCost(3, cfg.Economy) == 210);
        Check("star 3 on clean fast solve", EconomyModel.Stars(0, 0, 20, 20, cfg.Economy) == 3);
        Check("star 1 after 2 hints", EconomyModel.Stars(2, 0, 20, 20, cfg.Economy) == 1);
        Check("level coins at flow 1.6", EconomyModel.LevelCoins(3, 0, 1.6, 0, cfg.Economy) == 61,
            EconomyModel.LevelCoins(3, 0, 1.6, 0, cfg.Economy).ToString());
    }

    private static void TestWordTrie()
    {
        var trie = new WordTrie();
        foreach (string w in new[] { "cat", "car", "cart", "act", "dog", "tea", "eat" }) trie.Add(w);
        Check("trie contains", trie.Contains("cart") && !trie.Contains("ca"));
        Check("trie prefix", trie.HasPrefix("car") && !trie.HasPrefix("xyz"));
        Check("trie rejects bad chars", !trie.Contains("c@t"));

        var words = trie.EnumerateFromLetters("cart");
        Check("enumerate from letters", words.Contains("cart") && words.Contains("cat") && !words.Contains("dog"),
            string.Join(",", words));

        // A single wild must cover exactly ONE missing letter.
        var wildWords = trie.EnumerateFromLetters("ca*");
        Check("single wild substitutes one letter", wildWords.Contains("cat") && wildWords.Contains("car"),
            string.Join(",", wildWords));
        Check("wild cannot be reused", !wildWords.Contains("cart"), string.Join(",", wildWords));

        string tmp = Path.Combine(Path.GetTempPath(), $"lexiforge_trie_{Guid.NewGuid():N}.dat");
        trie.Save(tmp);
        var reloaded = WordTrie.Load(tmp);
        Check("binary image round-trip", reloaded.Contains("cart") && reloaded.HasPrefix("do") &&
            reloaded.EnumerateFromLetters("act").Contains("cat"));
        File.Delete(tmp);
    }

    private static void TestAdPolicy(BalanceConfig cfg)
    {
        var s = new AdPolicyState
        {
            LifetimeForges = 50, Sessions = 5, ForgesSinceLastInterstitial = 10,
            LastInterstitialAt = 0, LastRewardedAt = 0
        };
        Check("interstitial allowed when rules pass", AdPolicy.CanShowInterstitial(s, cfg.AdPolicy, 10_000, out _));
        s.LastRewardedAt = 9_950;
        Check("interstitial suppressed near rewarded",
            !AdPolicy.CanShowInterstitial(s, cfg.AdPolicy, 10_000, out string why) && why == "near_rewarded", why);
        s.LastRewardedAt = 0;
        s.IsForgePassOwner = true;
        Check("Forge Pass hides interstitials", !AdPolicy.CanShowInterstitial(s, cfg.AdPolicy, 10_000, out _));
        s.IsForgePassOwner = false;
        s.InRun = true;
        Check("never during a run", !AdPolicy.CanShowInterstitial(s, cfg.AdPolicy, 10_000, out _));
        s.InRun = false;

        cfg.Placements.TryGetValue("R_FREEZE", out var freeze);
        Check("R_FREEZE daily cap is 1", freeze?.DailyCap == 1, freeze?.DailyCap.ToString());
        var clean = new AdPolicyState { LifetimeForges = 50, Sessions = 5 };
        Check("rewarded first use allowed",
            AdPolicy.CanShowRewarded("R_FREEZE", clean, cfg.AdPolicy, cfg.Placements, 100_000, out _));
        AdPolicy.RecordRewarded(clean, 100_000, "R_FREEZE");
        Check("rewarded placement cap enforced",
            !AdPolicy.CanShowRewarded("R_FREEZE", clean, cfg.AdPolicy, cfg.Placements, 200_000, out string capWhy)
            && capWhy == "placement_cap", capWhy);
        AdPolicy.ResetDaily(clean);
        Check("daily reset clears caps", clean.PlacementUsesToday.Count == 0);
    }

    /// <summary>
    /// GDD 7.2: every rewarded ad on EVERY placement mints a Mark (6/day cap,
    /// +1 on the 3rd ad). This is the formula the 30-day Monte Carlo assumes;
    /// the P0 UI once passed marks=0 from all three placements and made the
    /// whole Patron Board economy inert. Verified here so it cannot regress.
    /// </summary>
    private static void TestMarkGrant(BalanceConfig cfg)
    {
        Check("tuning: 1 mark per rewarded ad", cfg.Marks.PerRewardedAd == 1,
            cfg.Marks.PerRewardedAd.ToString());
        Check("tuning: 6/day mark cap", cfg.Marks.DailyCapFromAds == 6,
            cfg.Marks.DailyCapFromAds.ToString());
        Check("tuning: bonus on 3rd ad", cfg.Marks.BonusMarkEveryNAd == 3,
            cfg.Marks.BonusMarkEveryNAd.ToString());

        // Walk a full day. AdManager increments the impression counter BEFORE
        // GrantFromAd, so `ad` is the post-increment count exactly as in
        // BalanceSim.MintRewarded.
        int[] expected = { 1, 1, 2, 1, 1, 0, 0, 0 };
        int marksToday = 0, total = 0;
        for (int ad = 1; ad <= expected.Length; ad++)
        {
            MarkGrant.Result r = MarkGrant.ForRewardedAd(cfg.Marks, ad, marksToday);
            Check($"ad {ad} mints {expected[ad - 1]} mark(s)",
                r.MarksGranted == expected[ad - 1], r.MarksGranted.ToString());
            marksToday = r.MarksTodayAfter;
            total += r.MarksGranted;
        }
        Check("daily cap never exceeded", marksToday <= cfg.Marks.DailyCapFromAds,
            marksToday.ToString());
        Check("6 marks max per day (matches simulator)", total == 6, total.ToString());
        Check("no mint once capped",
            MarkGrant.ForRewardedAd(cfg.Marks, 9, marksToday).MarksGranted == 0);

        // The bonus is one-time on the Nth ad, not recurring every Nth ad.
        var bonus = new MarkConfig { PerRewardedAd = 1, DailyCapFromAds = 100, BonusMarkEveryNAd = 3 };
        Check("bonus is one-time, not every-Nth",
            MarkGrant.ForRewardedAd(bonus, 6, 0).MarksGranted == 1);

        // Amounts come from tuning, never hardcoded in a view.
        var noBonus = new MarkConfig { PerRewardedAd = 1, DailyCapFromAds = 6, BonusMarkEveryNAd = 0 };
        Check("bonus can be disabled by tuning",
            MarkGrant.ForRewardedAd(noBonus, 3, 0).MarksGranted == 1);
        var doubleMint = new MarkConfig { PerRewardedAd = 2, DailyCapFromAds = 6, BonusMarkEveryNAd = 0 };
        Check("per-ad mint comes from tuning",
            MarkGrant.ForRewardedAd(doubleMint, 1, 0).MarksGranted == 2);

        // Cap clamps a large configured mint; hostile persisted values are safe.
        var bigMint = new MarkConfig { PerRewardedAd = 99, DailyCapFromAds = 6, BonusMarkEveryNAd = 0 };
        Check("cap clamps base mint",
            MarkGrant.ForRewardedAd(bigMint, 1, 4).MarksGranted == 2);
        Check("negative marksToday clamped",
            MarkGrant.ForRewardedAd(cfg.Marks, 1, -5).MarksGranted == 1);
    }

    private static void TestRuneDraft(BalanceConfig cfg)
    {
        var draft = new RuneDraft(cfg.Runes, cfg.Draft);
        var tutorial = draft.Offer(1234, 1, true, Array.Empty<string>());
        Check("tutorial first draft is scripted",
            tutorial.Select(r => r.Id).SequenceEqual(cfg.Draft.TutorialFirstDraft.Take(3)),
            string.Join(",", tutorial.Select(r => r.Id)));
        Check("tutorial offer has a non-score option", tutorial.Any(r => r.Kind != RuneKind.Score));

        for (int seed = 0; seed < 200; seed++)
        {
            var offer = draft.Offer(seed, 3, false, Array.Empty<string>());
            if (offer.Count != 3 || offer.Distinct().Count() != 3 || offer.All(r => r.Kind == RuneKind.Score))
            {
                Check("draft offer invariant across 200 seeds", false, $"seed {seed}: {string.Join(",", offer.Select(r => r.Id))}");
                return;
            }
        }
        Check("draft offer invariant across 200 seeds", true);

        var fx = RuneEffects.From(cfg.Runes.Where(r =>
            new[] { "prism", "anvil", "gale", "anchor", "lens" }.Contains(r.Id)), cfg.Draft);
        Check("prism doubles bonus jar", Math.Abs(fx.BonusJarMultiplier - 2.0) < 0.001);
        Check("anvil adds free hint", fx.ExtraFreeHints == 1);
        Check("gale adds wild", fx.WildTiles == 1);
        Check("anchor reduces heat", Math.Abs(fx.HeatGainReduction - 3.0) < 0.001);
        Check("lens reveals lengths", fx.RevealSlotLengths);
        Check("build delta clamped to +30%",
            Math.Abs(RuneEffects.From(cfg.Runes, cfg.Draft).CoinDeltaPct - cfg.Draft.MaxBuildCoinDeltaPct) < 0.001,
            RuneEffects.From(cfg.Runes, cfg.Draft).CoinDeltaPct.ToString("F3"));
        Check("echo pays per echoing letter",
            Math.Abs(RuneEffects.From(cfg.Runes.Where(r => r.Id == "echo"), cfg.Draft)
                .EchoCoinsPerLetter - 1.0) < 0.001);
    }

    private static void TestSaveData()
    {
        var save = new SaveData { Coins = 500, Marks = 3, ConfigVersion = "test" };
        save.RecordForge(1, 2);
        save.RecordForge(1, 3);
        save.RecordForge(2, 1);
        Check("save keeps best stars", save.StarsByForge[1] == 3 && save.TotalStars == 4);
        string json = save.ToJson();
        var loaded = SaveData.FromJson(json);
        Check("save round-trip", loaded.Coins == 500 && loaded.Marks == 3 && loaded.TotalStars == 4);
        Check("save validates", loaded.IsValid(out string reason), reason);

        var old = SaveData.FromJson("{\"schemaVersion\":1,\"coins\":42}");
        Check("v1 migration", old.SchemaVersion == SaveData.CurrentSchema && old.Coins == 42 &&
            old.StarsByForge.Count == 0 && !old.ConsentResolved);

        long day2 = 2 * 86_400L + 10;
        Check("daily rollover", old.RollDaily(day2) && old.AdDay.DayNumber == 2);
        Check("daily rollover idempotent", !old.RollDaily(day2));
    }

    private static ForgeData MakeTestForge()
    {
        // bank "forge": forge / frog / gofer / go / for / fog / ore ...
        var forge = new ForgeData
        {
            ForgeId = 1,
            Theme = "Emberworks",
            LetterBank = "forge",
            SolutionWords = new List<string> { "fog", "ore", "frog", "gofer" },
            KeystoneWord = "gofer",
            Slots = new List<GridSlot>
            {
                new() { Word = "fog",   Row = 0, Column = 0, Direction = "h" },
                new() { Word = "ore",   Row = 2, Column = 2, Direction = "h" },
                new() { Word = "frog",  Row = 0, Column = 0, Direction = "v" },
                new() { Word = "gofer", Row = 4, Column = 1, Direction = "h", IsKeystone = true }
            },
            DifficultyIndex = 10,
            ParTimeSeconds = 26,
            Seed = 1
        };
        forge.Checksum = forge.ComputeChecksum();
        return forge;
    }

    private static void TestForgeSession(BalanceConfig cfg)
    {
        var forge = MakeTestForge();
        var dict = new WordTrie();
        // "cog" is a real dictionary word but needs a 'c' that bank "forge" cannot
        // supply; "ergo" is in the dictionary AND spellable from the bank.
        foreach (string w in new[] { "fog", "ore", "frog", "gofer", "for", "go", "ergo", "roe", "ref", "ogre", "cog" })
            dict.Add(w);
        var session = new ForgeSession(forge, dict, cfg, RuneEffects.None);

        Check("keystone locked at start", !session.KeystoneUnlocked);
        var locked = session.Submit("gofer");
        Check("keystone submission refused without penalty",
            locked.Kind == WordSubmissionKind.KeystoneLocked && !locked.IsPenalty && session.Flow == 1.0);

        var bonus = session.Submit("roe");
        Check("bonus word accepted into jar", bonus.Kind == WordSubmissionKind.BonusWord && session.BonusJar.Count == 1);
        Check("bonus word does not advance clean streak", session.CleanStreak == 0);
        // UI-66: a jar duplicate is NOT a forged word. Reusing AlreadyFound here
        // is exactly what produced the CARE "Already forged" report.
        WordSubmission jarRepeat = session.Submit("roe");
        Check("bonus word duplicate is AlreadyInJar", jarRepeat.Kind == WordSubmissionKind.AlreadyInJar,
            jarRepeat.Kind.ToString());
        Check("bonus word duplicate is a repeat, not a penalty", jarRepeat.IsRepeat && !jarRepeat.IsPenalty);

        var first = session.Submit("fog");
        Check("solution accepted", first.Kind == WordSubmissionKind.SolutionWord);
        Check("flow steps +0.15", Math.Abs(session.Flow - 1.15) < 0.001, session.Flow.ToString("F2"));
        session.Submit("ore");
        Check("flow steps again", Math.Abs(session.Flow - 1.30) < 0.001, session.Flow.ToString("F2"));

        var wrong = session.Submit("zzz");
        Check("invalid word not constructible", wrong.Kind == WordSubmissionKind.NotConstructible);
        Check("wrong word resets Flow to 1.0", session.Flow == 1.0 && session.WrongAttempts == 1);
        Check("wrong word keeps found words", session.FoundSolutions.Count == 2);
        // Constructibility is checked before the dictionary lookup. A dictionary
        // word the bank CAN spell is a bonus word; one it cannot spell is a penalty.
        Check("in-dictionary constructible word becomes a bonus",
            session.Submit("ergo").Kind == WordSubmissionKind.BonusWord);
        Check("in-dictionary-but-not-constructible rejected",
            session.Submit("cog").Kind == WordSubmissionKind.NotConstructible);
        Check("bonus jar holds both accepted bonus words", session.BonusJar.Count == 2);

        session.Tick(10);
        var hint = session.UseHint(HintSource.Ad, 0);
        Check("ad hint granted at no coin cost", hint.Granted && hint.CoinCost == 0 && hint.Letter != '\0', hint.Reason);
        Check("revealed letter appears in mask", session.MaskFor(hint.SlotIndex).Contains(char.ToUpperInvariant(hint.Letter)));
        Check("hint disables 3 stars", session.HintsUsed == 1);

        session.Tick(50);
        Check("free nudge triggers after 45s", session.TryGetNudge(out char nudge, out _, out _) && nudge != '\0');
        Check("free nudge does not count as hint", session.HintsUsed == 1);

        var coinHint = session.UseHint(HintSource.Coin, 0);
        Check("coin hint blocked when broke", !coinHint.Granted && coinHint.Reason == "insufficient_coins");

        session.Submit("frog");
        Check("keystone unlocked after all others", session.KeystoneUnlocked);
        var last = session.Submit("gofer");
        Check("forge completes", last.ForgeCompleted && session.Completed);
        Check("no submit after completion", session.Submit("fog").Kind == WordSubmissionKind.ForgeAlreadyComplete);
        Check("outcome has coins and stars", session.Outcome!.TotalCoins > 0 && session.Outcome.Result.Stars is >= 1 and <= 3,
            session.Outcome.TotalCoins.ToString());
        Check("outcome records both bonus words", session.Outcome.BonusWords.Count == 2,
            string.Join(",", session.Outcome.BonusWords));

        var galeSession = new ForgeSession(forge, dict, cfg,
            RuneEffects.From(cfg.Runes.Where(r => r.Id == "gale"), cfg.Draft));
        Check("gale wild allows one extra letter", ForgeSession.IsConstructible("gofer", "forg*"));
    }

    /// <summary>
    /// Regression for the player report: MAT, EAT, Give Up (reveals MEAT),
    /// KEEP FORGING, then MEAT -> "Not in the dictionary". Submit() used to check
    /// the optional bonus-word trie BEFORE solution membership, so ANY drift
    /// between pack_01.json and words_en.dat made the game reject the exact
    /// answer it had just shown. The pack and the trie are built by different
    /// tools (LEVEL_DESIGN.md "Ordering trap"), so this must be impossible.
    /// </summary>
    private static void TestSolutionWordsAreAuthoritative(BalanceConfig cfg)
    {
        var forge = new ForgeData
        {
            ForgeId = 777,
            Theme = "Emberworks",
            LetterBank = "team",
            SolutionWords = new List<string> { "eat", "mat", "meat" },
            KeystoneWord = "meat",
            Slots = new List<GridSlot>
            {
                new() { Word = "meat", Row = 0, Column = 1, Direction = "h", IsKeystone = true },
                new() { Word = "eat",  Row = 0, Column = 2, Direction = "v" },
                new() { Word = "mat",  Row = 2, Column = 0, Direction = "h" }
            },
            DifficultyIndex = 6.5,
            ParTimeSeconds = 21.6,
            Seed = 15838
        };

        // Simulate a STALE words_en.dat: "meat" is missing even though the pack
        // ships it as the keystone. All other words are present, exactly like the
        // reported session where MAT and EAT were happily accepted.
        var dict = new WordTrie();
        foreach (string w in new[] { "eat", "mat", "team", "tame", "tea", "ate" }) dict.Add(w);
        Check("repro: dictionary really lacks 'meat'", !dict.Contains("meat"));

        var session = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        Check("repro: MAT accepted", session.Submit("mat").Kind == WordSubmissionKind.SolutionWord);
        Check("repro: EAT accepted", session.Submit("eat").Kind == WordSubmissionKind.SolutionWord);
        WordSubmission keystone = session.Submit("meat");
        Check("repro: revealed MEAT accepted despite stale trie",
            keystone.Kind == WordSubmissionKind.SolutionWord && keystone.ForgeCompleted && session.Completed,
            keystone.Kind.ToString());
        Check("repro: reveal flow keeps keystone gating",
            new ForgeSession(forge, dict, cfg, RuneEffects.None)
                .Submit("meat").Kind == WordSubmissionKind.KeystoneLocked);

        // The trie still governs OPTIONAL words: dropping the check entirely
        // would turn every anagram of the bank into a free bonus coin.
        var fresh = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        Check("non-solution word still needs the dictionary",
            fresh.Submit("ema").Kind == WordSubmissionKind.NotInDictionary);
        Check("dictionary bonus word still pays out",
            fresh.Submit("team").Kind == WordSubmissionKind.BonusWord);
    }

    /// <summary>
    /// Replay of the "CARE / Already Forged" report. The real play log shows
    /// ARC, ERA forged; car/ace/ear accepted into the Bonus Jar; then repeated
    /// 3-tile drags of car/ace/ear while the player was trying to spell the
    /// revealed 4-letter keystone. care was NEVER submitted — but a generic
    /// "Already forged" toast made it look like the revealed answer was refused.
    /// </summary>
    private static void TestRevealedKeystoneAfterDuplicateBonus(BalanceConfig cfg)
    {
        var forge = new ForgeData
        {
            ForgeId = 3,
            Theme = "Emberworks",
            LetterBank = "race",
            SolutionWords = new List<string> { "arc", "era", "care" },
            KeystoneWord = "care",
            Slots = new List<GridSlot>
            {
                new() { Word = "care", Row = 0, Column = 0, Direction = "h", IsKeystone = true },
                new() { Word = "arc",  Row = 0, Column = 1, Direction = "v" },
                new() { Word = "era",  Row = 0, Column = 3, Direction = "v" }
            },
            DifficultyIndex = 6.5,
            ParTimeSeconds = 21.644,
            Seed = 23757
        };

        // Deliberately omit "care" as well: this replay must prove the stale-trie
        // defence from the MEAT report AND the duplicate-verdict fix together.
        var dict = new WordTrie();
        foreach (string w in new[] { "arc", "era", "ace", "ear", "car", "race", "are", "rec" }) dict.Add(w);
        Check("repro CARE: stale trie really lacks 'care'", !dict.Contains("care"));

        var s = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        Check("repro CARE: ARC accepted", s.Submit("arc").Kind == WordSubmissionKind.SolutionWord);
        Check("repro CARE: ERA accepted", s.Submit("era").Kind == WordSubmissionKind.SolutionWord);
        Check("repro CARE: keystone unlocked with 2 of 3 forged", s.KeystoneUnlocked);

        Check("repro CARE: CAR accepted into the jar",
            s.Submit("car").Kind == WordSubmissionKind.BonusWord);

        double flowBefore = s.Flow;
        WordSubmission jarRepeat = s.Submit("car");
        Check("repro CARE: duplicate bonus is AlreadyInJar, not AlreadyFound",
            jarRepeat.Kind == WordSubmissionKind.AlreadyInJar, jarRepeat.Kind.ToString());
        Check("repro CARE: jar duplicate is a repeat, never a penalty",
            jarRepeat.IsRepeat && !jarRepeat.IsPenalty);
        Check("repro CARE: jar duplicate keeps Flow and clean streak",
            Math.Abs(s.Flow - flowBefore) < 0.001 && s.CleanStreak == 2,
            $"flow={s.Flow} streak={s.CleanStreak}");
        Check("repro CARE: jar duplicate does not double-count", s.BonusJar.Count == 1);

        // A duplicate SOLUTION word is a different verdict, and it knows its slot.
        WordSubmission forgedAgain = s.Submit("arc");
        Check("repro CARE: duplicate solution is AlreadyFound",
            forgedAgain.Kind == WordSubmissionKind.AlreadyFound, forgedAgain.Kind.ToString());
        Check("repro CARE: duplicate solution carries its slot index", forgedAgain.SlotIndex >= 0);
        Check("repro CARE: both duplicate kinds are non-punitive repeats",
            forgedAgain.IsRepeat && !forgedAgain.IsPenalty);
        Check("repro CARE: repeats never counted as wrong attempts", s.WrongAttempts == 0,
            s.WrongAttempts.ToString());

        // UI-65: the revealed answer is still playable after all of the above.
        WordSubmission keystone = s.Submit("care");
        Check("repro CARE: revealed keystone accepted after repeats and with a stale trie",
            keystone.Kind == WordSubmissionKind.SolutionWord && keystone.ForgeCompleted && s.Completed,
            keystone.Kind.ToString());
        Check("repro CARE: keystone gating still held before the reveal",
            new ForgeSession(forge, dict, cfg, RuneEffects.None)
                .Submit("care").Kind == WordSubmissionKind.KeystoneLocked);
    }

    private static void TestRunState(BalanceConfig cfg)
    {
        var dict = new WordTrie();
        foreach (string w in new[] { "fog", "ore", "frog", "gofer", "roe" }) dict.Add(w);
        var forge = MakeTestForge();
        var run = new RunState(cfg);
        run.StartRun(777, true);

        Check("draft after forge 1", !run.IsDraftForge); // ForgesInRun == 0 at start
        ForgeOutcome outcome = FastComplete(forge, dict, cfg, 0.5);
        var ended = run.ApplyForge(outcome, 10);
        Check("one forge counted", run.ForgesInRun == 1);
        Check("draft offered after forge 1", run.IsDraftForge);
        Check("run not over", ended is null && !run.IsRunOver);
        Check("heat rises", run.Heat > 0, run.Heat.ToString("F2"));

        var offer = run.DraftOffer();
        Check("first draft ever is scripted", offer.Select(r => r.Id).SequenceEqual(cfg.Draft.TutorialFirstDraft.Take(3)));
        run.Draft(offer[0]);
        Check("rune owned", run.OwnedRunes.Count == 1);
        Check("run score accumulates", run.RunScore == outcome.TotalCoins);

        double hot = run.Heat;
        run.CoolForge();
        Check("cool forge reduces heat by 25", Math.Abs(run.Heat - Math.Max(0, hot - 25)) < 0.001);

        for (int i = 0; i < 8 && !run.IsRunOver; i++)
            ended = run.ApplyForge(FastComplete(forge, dict, cfg, 1.5), 40);
        Check("run ends with a reward payload", ended is not null && ended.Forges >= 1 && ended.TotalScore > 0);
        Check("run stops at 8 forges or heat 100", run.IsRunOver);
    }

    /// <summary>
    /// Regression cover for the run/draft/session behaviours the P0 UI depends
    /// on. Each of these was a real defect found while wiring the screens.
    /// </summary>
    private static void TestP0UiContracts(BalanceConfig cfg)
    {
        // 1. "Cool the forge" must CONTINUE the run, not immediately end it again
        //    on the very next forge because the forge-count cap was still hit.
        var dict = new WordTrie();
        foreach (string w in new[] { "fog", "ore", "frog", "gofer" }) dict.Add(w);
        var forge = MakeTestForge();
        var run = new RunState(cfg);
        run.StartRun(4242, false);
        for (int i = 0; i < cfg.Heat.MaxForgesPerRun && !run.IsRunOver; i++)
            run.ApplyForge(FastComplete(forge, dict, cfg, 1.0), 10);
        Check("forge cap ends the run", run.IsRunOver);
        run.CoolForge();
        Check("cool forge reopens a run ended by the forge cap", !run.IsRunOver,
            $"forges={run.ForgesInRun} heat={run.Heat:F1}");
        Check("cooling keeps run history intact", run.ForgesInRun == cfg.Heat.MaxForgesPerRun);
        Check("next forge after cooling does not end the run",
            run.ApplyForge(FastComplete(forge, dict, cfg, 1.0), 10) is null);

        // 2. A PAID reroll used to be a no-op (RunState returned DraftOffer()
        //    whenever no free reroll was left) even though the currency was gone.
        bool anyDifferent = false;
        for (int seed = 1; seed <= 20 && !anyDifferent; seed++)
        {
            var r = new RunState(cfg);
            r.StartRun(seed, false);
            string before = string.Join(",", r.DraftOffer().Select(x => x.Id));
            string after = string.Join(",", r.RerollOffer().Select(x => x.Id));
            if (before != after) anyDifferent = true;
        }
        Check("paid reroll produces a genuinely new offer", anyDifferent);

        var mirror = new RunState(cfg);
        mirror.StartRun(99, false);
        mirror.Draft(cfg.Runes.First(r => r.Id == "mirror"));
        Check("mirror grants a free reroll", mirror.CanRerollFree);
        mirror.RerollOffer();
        Check("free reroll is consumed exactly once", !mirror.CanRerollFree);

        // 3. FirstRunEver must survive the first draft (the tutorial hint on
        //    forge 4 relies on it; _firstDraftEver does not).
        var tutorial = new RunState(cfg);
        tutorial.StartRun(1, true);
        tutorial.Draft(tutorial.DraftOffer()[0]);
        Check("first-run flag survives the first draft", tutorial.FirstRunEver);

        // 4. Struggle reveal: free letter after 3 wrong attempts, must NOT cost
        //    a hint (which would silently downgrade the star rating).
        var session = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        session.Submit("zzz");
        session.Submit("zzz");
        session.Submit("zzz");
        Check("struggle reveal offered after 3 wrong words", session.ConsecutiveWrongAttemptsReached);
        Check("struggle reveal grants a letter",
            session.TryGrantStruggleReveal(out char letter, out int slot, out _) && letter != '\0' && slot >= 0);
        Check("struggle reveal is not counted as a hint", session.HintsUsed == 0);
        Check("struggle reveal is one-off per forge",
            !session.ConsecutiveWrongAttemptsReached &&
            !session.TryGrantStruggleReveal(out _, out _, out _));
    }

    /// <summary>
    /// Echo regression cover. The old effect keyed on words with a repeated
    /// letter, which Chapter I banks can never produce (banks are validated to
    /// have distinct tiles), and the raised Flow cap was clamped away by
    /// EconomyModel.LevelCoins anyway — so the rune paid exactly 0 coins even
    /// when it fired. It now pays per letter of a word that crosses an
    /// already-filled slot, which every forge is contracted to have.
    /// </summary>
    private static void TestEchoRune(BalanceConfig cfg)
    {
        // bank "star" -> art / rat / star, with two real grid intersections.
        var forge = new ForgeData
        {
            ForgeId = 1,
            Theme = "Emberworks",
            LetterBank = "star",
            SolutionWords = new List<string> { "art", "rat", "star" },
            KeystoneWord = "star",
            Slots = new List<GridSlot>
            {
                new() { Word = "star", Row = 0, Column = 0, Direction = "h", IsKeystone = true },
                new() { Word = "art",  Row = 0, Column = 2, Direction = "v" },
                new() { Word = "rat",  Row = 2, Column = 0, Direction = "h" }
            },
            DifficultyIndex = 6.5,
            ParTimeSeconds = 21.6,
            Seed = 1
        };

        var dict = new WordTrie();
        foreach (string w in new[] { "art", "rat", "star", "tar", "arts" }) dict.Add(w);

        ForgeOutcome Run(RuneEffects fx)
        {
            var s = new ForgeSession(forge, dict, cfg, fx);
            s.Submit("art");   // first word: nothing filled yet, no echo
            s.Submit("rat");   // crosses "art" at the shared T
            s.Submit("star");  // crosses "art" at the shared A
            return s.Outcome!;
        }

        var echo = RuneEffects.From(cfg.Runes.Where(r => r.Id == "echo"), cfg.Draft);
        var withEcho = Run(echo);
        var without = Run(RuneEffects.None);

        // rat(3) + star(4) echo -> 7 letters.
        Check("echo counts only letters already on the grid", withEcho.RuneBonusCoins == 7,
            withEcho.RuneBonusCoins.ToString());
        Check("echo pays nothing without the rune", without.RuneBonusCoins == 0,
            without.RuneBonusCoins.ToString());
        Check("echo is additive on top of base coins",
            withEcho.RuneBonusCoins == 7 && withEcho.TotalCoins > without.TotalCoins,
            $"{withEcho.TotalCoins} vs {without.TotalCoins}");
    }

    private static ForgeOutcome FastComplete(ForgeData forge, WordTrie dict, BalanceConfig cfg, double parRatio)
    {
        var s = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        foreach (string word in forge.SolutionWords) s.Submit(word);
        s.Tick(forge.ParTimeSeconds * parRatio);
        // Rebuild outcome through a fresh session that ticks before finishing,
        // so solve-time/par feeding stars is representative.
        var t = new ForgeSession(forge, dict, cfg, RuneEffects.None);
        t.Tick(forge.ParTimeSeconds * parRatio);
        foreach (string word in forge.SolutionWords) t.Submit(word);
        return t.Outcome!;
    }

    private static void TestGeneratedForges(BalanceConfig cfg, string dataDir)
    {
        string forgesDir = Path.Combine(dataDir, "forges");
        if (!Directory.Exists(forgesDir))
        {
            Check("generated forge catalogue exists", false, forgesDir);
            return;
        }
        string triePath = Path.Combine(dataDir, "words_en.dat");
        Check("shipped binary dictionary exists", File.Exists(triePath), triePath);
        if (!File.Exists(triePath)) return;

        WordTrie dict = WordTrie.Load(triePath);
        var forges = ForgeCatalog.LoadAll(forgesDir);
        Check("P0 catalogue has 20 forges", forges.Count == 20, forges.Count.ToString());
        Check("forge ids are 1..20 unique",
            forges.Select(f => f.ForgeId).Distinct().Count() == forges.Count &&
            forges.Min(f => f.ForgeId) == 1 && forges.Max(f => f.ForgeId) == 20);
        Check("no duplicate forge checksums", forges.Select(f => f.Checksum).Distinct().Count() == forges.Count);

        // The pack JSON and words_en.dat come from DIFFERENT generators. A gap
        // here used to surface in play as "Not in the dictionary" for a word
        // Give Up had just revealed, so CI must catch it at the source.
        var gaps = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ForgeData forge in forges)
        {
            foreach (string word in forge.SolutionWords)
                if (!dict.Contains(word)) gaps.Add(word.ToLowerInvariant());
            foreach (GridSlot slot in forge.Slots)
                if (!dict.Contains(slot.Word)) gaps.Add(slot.Word.ToLowerInvariant());
        }
        Check("every shipped answer is in words_en.dat", gaps.Count == 0, string.Join(",", gaps));

        var blocklist = new HashSet<string>(StringComparer.Ordinal);
        var common = LexiForge.Tools.WordList.Common;
        int errorCount = 0;
        foreach (var forge in forges)
        {
            var errors = ForgeValidator.Validate(forge, dict, cfg, blocklist, common);
            if (errors.Count > 0)
            {
                errorCount++;
                if (errorCount <= 5) Console.WriteLine($"  forge {forge.ForgeId}: {string.Join("; ", errors)}");
            }
        }
        Check("all 20 forges pass content contract + independent solver", errorCount == 0, $"{errorCount} invalid");

        // Every generated forge must be solvable through the real ForgeSession.
        int unsolvable = 0;
        foreach (var forge in forges)
        {
            var s = new ForgeSession(forge, dict, cfg, RuneEffects.None);
            foreach (string word in forge.SolutionWords)
            {
                var r = s.Submit(word);
                if (!r.IsSuccess && r.Kind != WordSubmissionKind.SolutionWord) { unsolvable++; break; }
            }
            if (!s.Completed) unsolvable++;
        }
        Check("all 20 forges solvable through ForgeSession", unsolvable == 0, unsolvable.ToString());
    }

    // ------------------------------------------------------------------ helpers
    /// <summary>
    /// Audio catalogue contract (design/AUDIO_DESIGN.md). The shipped game, the
    /// greybox generator and this harness all read the SAME json, so a typo in an
    /// event id or a missing .wav breaks CI instead of silently going mute.
    /// </summary>
    private static void TestAudioCatalog(string root)
    {
        string path = Path.Combine(root, "godot", "data", "audio_events.json");
        AudioEventTable table = AudioEventTable.Load(path);

        bool valid = table.Validate(out List<string> errors);
        Check("audio catalog validates", valid, string.Join(" | ", errors));
        Check("audio catalog has events", table.Events.Count >= 30, table.Events.Count.ToString());
        Check("audio buses are Music/SFX/UI/AMB",
            table.Buses.Select(b => b.Name).OrderBy(x => x)
                .SequenceEqual(new[] { "AMB", "Music", "SFX", "UI" }));
        Check("voice pool covers one-shot cap", table.MaxOneShots <= table.VoicePoolSize);
        Check("all stream paths are unique res://",
            table.AllStreamPaths().All(p => p.StartsWith("res://", StringComparison.Ordinal)));

        // Every bundled asset must exist. This is why the greybox generator reads
        // the catalog rather than maintaining a second file list.
        bool assets = table.ValidateAssets(Path.Combine(root, "godot"), out List<string> missing);
        Check("every catalogued audio asset exists", assets,
            missing.Count == 0 ? "" : $"{missing.Count} missing, first: {missing[0]}");

        // Signature mechanics: spelling ladder and Flow motif.
        var tile = table.Find("tile.pick")!;
        var word = table.Find("word.solution")!;
        Check("tile.pick uses letter-index ladder", tile.Pitch.Mode == AudioPitchMode.Ladder);
        Check("spelling ladder rises", tile.Pitch.PitchScale(7, 0) > tile.Pitch.PitchScale(1, 0));
        Check("Flow motif rises with Flow", word.Pitch.PitchScale(250, 0) > word.Pitch.PitchScale(100, 0));
        Check("keystone is never voice-stolen", table.Find("word.keystone")!.Priority == 3);
        Check("reject is quieter than solution",
            table.Find("word.reject")!.GainDb < word.GainDb);

        // Anti-repetition: no immediate repeat over a long run of tile taps.
        var picker = new AudioVariationPicker(1234);
        int last = -1;
        bool repeated = false;
        for (int i = 0; i < 200; i++)
        {
            int v = picker.Next(tile.Streams.Length, AudioVariationMode.RoundRobinNoRepeat, i);
            if (v == last) repeated = true;
            last = v;
        }
        Check("tile variations never repeat back-to-back", !repeated);

        // Adaptive music band is monotonic and bounded.
        double low = AudioEventTable.AdaptiveGain(table.Music.Adaptive.Heat, 0);
        double mid = AudioEventTable.AdaptiveGain(table.Music.Adaptive.Heat, 70);
        double high = AudioEventTable.AdaptiveGain(table.Music.Adaptive.Heat, 100);
        Check("Heat stem gain is monotonic", low < mid && mid < high,
            $"{low:F1} / {mid:F1} / {high:F1}");
    }

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ok  {name}"); }
        else { Failures.Add($"{name}{(detail is null ? "" : $" [{detail}]")}"); Console.WriteLine($"  FAIL {name} {detail}"); }
    }

    private static string LocateRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "design", "balance_config.json"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("Pass the LexiForge repo root as the first argument.");
    }
}
