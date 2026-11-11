using System;
using System.Collections.Generic;
using System.Linq;

namespace LexiForge.Core;

/// <summary>
/// Content-contract validation + an INDEPENDENT solver (GDD 6.3 step 8).
/// Called by LevelGen at build time and by CoreTests in CI; must not trust any
/// data produced by the generator.
/// </summary>
public static class ForgeValidator
{
    public static double DifficultyFromContent(ForgeData forge, DifficultyWeights w,
                                               IReadOnlySet<string> commonWords, bool isBreather)
    {
        double avgLen = forge.SolutionWords.Count == 0
            ? 3.0 : forge.SolutionWords.Average(x => x.Length);
        double rarity = forge.SolutionWords.Count == 0
            ? 0.0 : forge.SolutionWords.Count(x => !commonWords.Contains(x)) / (double)forge.SolutionWords.Count;
        var shape = new LevelShape(forge.ForgeId, forge.SolutionWords.Count,
                                   forge.LetterBank.Length, avgLen, rarity, isBreather);
        return DifficultyModel.Index(shape, w);
    }

    public static List<string> Validate(ForgeData forge, WordTrie dictionary,
                                        BalanceConfig cfg, IReadOnlySet<string> blocklist,
                                        IReadOnlySet<string> commonWords)
    {
        var errors = new List<string>();
        if (forge is null) { errors.Add("forge is null"); return errors; }

        if (!forge.HasValidChecksum()) errors.Add("checksum mismatch");
        if (forge.LetterBank.Length is < 3 or > 7) errors.Add($"bank size {forge.LetterBank.Length} outside 3..7");
        if (forge.LetterBank.Any(c => c is < 'a' or > 'z')) errors.Add("bank contains non a-z tile");
        if (forge.LetterBank.Distinct().Count() != forge.LetterBank.Length) errors.Add("bank has duplicate tiles");

        var words = forge.SolutionWords;
        if (words.Count < 3) errors.Add($"only {words.Count} solution words (min 3)");
        if (words.Distinct(StringComparer.Ordinal).Count() != words.Count) errors.Add("duplicate solution words");
        foreach (string word in words)
        {
            if (word.Length < ForgeSession.MinWordLength || word.Any(c => c is < 'a' or > 'z'))
                errors.Add($"invalid solution word '{word}'");
            if (blocklist.Contains(word)) errors.Add($"blocklisted word '{word}'");
            if (!dictionary.Contains(word)) errors.Add($"'{word}' not in shipped dictionary");
            if (!ForgeSession.IsConstructible(word, forge.LetterBank))
                errors.Add($"'{word}' not constructible from bank '{forge.LetterBank}'");
        }

        // --- the aha-word rule (GDD 3.2) ---
        int shortWords = words.Count(x => x.Length <= 4);
        if (shortWords < cfg.Content.MinShortWordsLengthLTE4)
            errors.Add($"only {shortWords} gimme words (need {cfg.Content.MinShortWordsLengthLTE4})");

        string keystone = forge.KeystoneWord;
        if (words.Count == 0 || !string.Equals(words[^1], keystone, StringComparison.Ordinal))
            errors.Add("keystone is not the LAST solution word");
        int longestOther = words.Where(x => x != keystone).Select(x => x.Length).DefaultIfEmpty(0).Max();
        if (keystone.Length <= longestOther)
            errors.Add($"keystone '{keystone}' is not uniquely the longest word");
        if (words.Count(x => x.Length == keystone.Length) != 1)
            errors.Add("more than one word ties the keystone length");

        // --- grid geometry ---
        if (forge.Slots.Count != words.Count) errors.Add($"slot count {forge.Slots.Count} != word count {words.Count}");
        var occupied = new Dictionary<(int, int), List<char>>();
        foreach (var slot in forge.Slots)
        {
            if (!words.Contains(slot.Word)) errors.Add($"slot word '{slot.Word}' is not a solution word");
            if (slot.IsVertical && slot.Direction != "v") errors.Add($"slot '{slot.Word}' direction flag mismatch");
            if (slot.Row < 0 || slot.Column < 0) errors.Add($"slot '{slot.Word}' has negative origin");
            foreach (var cell in slot.Cells())
            {
                var key = (cell.Row, cell.Column);
                if (!occupied.TryGetValue(key, out var letters))
                {
                    letters = new List<char>(2);
                    occupied[key] = letters;
                }
                letters.Add(cell.Letter);
            }
        }
        int conflicting = occupied.Count(kv => kv.Value.Distinct().Count() > 1);
        if (conflicting > 0) errors.Add($"{conflicting} intersection cells have conflicting letters");
        int intersections = occupied.Count(kv => kv.Value.Count > 1 && kv.Value.Distinct().Count() == 1);
        if (intersections < cfg.Content.MinGridIntersections)
            errors.Add($"only {intersections} intersections (need {cfg.Content.MinGridIntersections})");
        if (forge.Slots.Count(x => x.IsKeystone) != cfg.Content.KeystoneWords)
            errors.Add($"expected exactly {cfg.Content.KeystoneWords} keystone slot");
        var keystoneSlot = forge.Slots.FirstOrDefault(x => x.IsKeystone);
        if (keystoneSlot is not null && keystoneSlot.Word != keystone)
            errors.Add("keystone flag is on the wrong slot");

        // --- difficulty ---
        bool breather = forge.ForgeId % Math.Max(1, cfg.Shape.BreatherEveryN) == 0;
        double computedDI = DifficultyFromContent(forge, cfg.Weights, commonWords, breather);
        if (Math.Abs(computedDI - forge.DifficultyIndex) > 0.02)
            errors.Add($"DI {forge.DifficultyIndex:F2} != content DI {computedDI:F2}");
        var band = cfg.BandFor(forge.ForgeId);
        if (band is not null && !band.Contains(forge.DifficultyIndex))
            errors.Add($"DI {forge.DifficultyIndex:F2} outside pack band [{band.MinDI},{band.MaxDI}]");
        double expectedPar = DifficultyModel.ParTimeSeconds(forge.DifficultyIndex, cfg.ParTime);
        if (Math.Abs(expectedPar - forge.ParTimeSeconds) > 0.06)
            errors.Add($"parTime {forge.ParTimeSeconds:F2} != {expectedPar:F2}");

        // --- independent solver ---
        if (!SolveIndependently(forge, dictionary, out string solveReason))
            errors.Add($"independent solver: {solveReason}");

        return errors;
    }

    /// <summary>
    /// Re-derives every constructible word from the bank through the trie and
    /// confirms the solution set is fully reachable with bonus words left over.
    /// Deliberately independent of the generator's selection code.
    /// </summary>
    public static bool SolveIndependently(ForgeData forge, WordTrie dictionary, out string reason)
    {
        var reachable = new HashSet<string>(
            dictionary.EnumerateFromLetters(forge.LetterBank).Where(w => w.Length >= ForgeSession.MinWordLength),
            StringComparer.Ordinal);

        foreach (string word in forge.SolutionWords)
        {
            if (!reachable.Contains(word)) { reason = $"'{word}' unreachable from bank"; return false; }
        }
        int bonusAvailable = reachable.Count - forge.SolutionWords.Count;
        if (bonusAvailable < 2) { reason = $"only {bonusAvailable} bonus words available"; return false; }
        reason = "ok";
        return true;
    }
}
