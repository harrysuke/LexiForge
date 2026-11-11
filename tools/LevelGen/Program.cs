using System.Text;
using LexiForge.Core;

namespace LexiForge.Tools;

/// <summary>
/// Offline forge generator (GDD 6.3). NEVER runs on a device.
///
///   dotnet run --project godot/tools/LevelGen -- [repoRoot] [forgeCount]
///
/// Emits:
///   godot/data/forges/pack_01.json   deterministic P0 catalogue
///   godot/data/words_en.dat          compact trie image for runtime bonus words
///   godot/data/balance_config.json   copy of the single source of truth
///
/// Every forge is checked against ForgeValidator (content contract, checksum,
/// DI band, grid geometry) AND re-solved independently before it is written.
/// A single failure returns a non-zero exit code so CI cannot ship it.
///
/// KNOWN DESIGN INCONSISTENCY (surfaced, not silently patched):
/// GDD 6.1's shape curve specifies avgWordLength 3.32 at forge 1 with bankSize 3.
/// Under one-tile-per-letter anagram rules a word can never be longer than the
/// bank, so that pair is impossible. The shape curve is therefore treated as a
/// TARGET: the generator picks real words and computes DI from the ACTUAL content
/// (wordCount / bankSize / avgLen / rarity), then validates against the pack band.
/// </summary>
internal static class Program
{
    private const int MaxAttemptsPerForge = 4000;

    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : LocateRoot();
        int forgeCount = args.Length > 1 ? int.Parse(args[1]) : 20;

        string designDir = Path.Combine(root, "design");
        string dataDir = Path.Combine(root, "godot", "data");
        string forgesDir = Path.Combine(dataDir, "forges");
        Directory.CreateDirectory(forgesDir);

        BalanceConfig cfg = BalanceConfig.Load(Path.Combine(designDir, "balance_config.json"));
        WordTrieDefinition built = WordList.BuildTrie();
        WordTrie trie = built.Trie;
        IReadOnlySet<string> common = WordList.Common;
        IReadOnlySet<string> blocklist = WordList.Blocklist;

        Console.WriteLine($"LexiForge LevelGen | config={cfg.ConfigVersion} | dictionary={built.Words.Count} words | trie={trie.NodeCount} nodes");
        Console.WriteLine($"Band for P0: [{cfg.BandFor(1)?.MinDI},{cfg.BandFor(1)?.MaxDI}]  |  forges={forgeCount}");
        Console.WriteLine(new string('=', 96));

        trie.Save(Path.Combine(dataDir, "words_en.dat"));
        File.Copy(Path.Combine(designDir, "balance_config.json"),
                  Path.Combine(dataDir, "balance_config.json"), overwrite: true);

        var forges = new List<ForgeData>(forgeCount);
        for (int id = 1; id <= forgeCount; id++)
        {
            ForgeData? forge = Generate(id, cfg, trie, common, blocklist);
            if (forge is null)
            {
                Console.Error.WriteLine($"FATAL: could not generate a valid forge {id} after {MaxAttemptsPerForge} attempts.");
                return 2;
            }
            var errors = ForgeValidator.Validate(forge, trie, cfg, blocklist, common);
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"FATAL: forge {id} failed validation: {string.Join("; ", errors)}");
                return 3;
            }
            forges.Add(forge);
            Console.WriteLine($"  forge {id,2}  bank={forge.LetterBank,-7} words={forge.SolutionWords.Count} " +
                              $"keystone={forge.KeystoneWord,-7} DI={forge.DifficultyIndex,5:F2} " +
                              $"par={forge.ParTimeSeconds,5:F1}s intersections={forge.IntersectionCount()} " +
                              $"checksum={forge.Checksum}");
        }

        var band = cfg.BandFor(1);
        var pack = new ForgePack
        {
            PackId = 1,
            Theme = band?.Theme ?? "Emberworks",
            DifficultyBand = new[] { band?.MinDI ?? 5.0, band?.MaxDI ?? 18.0 },
            Forges = forges
        };
        string packPath = Path.Combine(forgesDir, "pack_01.json");
        File.WriteAllText(packPath, ForgeCatalog.ToJson(pack), new UTF8Encoding(false));
        Console.WriteLine(new string('-', 96));
        Console.WriteLine($"Wrote {forges.Count} forges -> {packPath}");
        Console.WriteLine($"Wrote dictionary  -> {Path.Combine(dataDir, "words_en.dat")}");
        Console.WriteLine("All forges pass the content contract and the independent solver.");
        return 0;
    }

    // -------------------------------------------------------------- generation
    private static ForgeData? Generate(int id, BalanceConfig cfg, WordTrie trie,
                                       IReadOnlySet<string> common, IReadOnlySet<string> blocklist)
    {
        LevelShape shape = DifficultyModel.ShapeFor(id, cfg.Shape);
        int targetWords = Math.Clamp(shape.WordCount, 3, 5);
        var band = cfg.BandFor(id);
        int families = WordList.Families.Count;

        for (int attempt = 0; attempt < MaxAttemptsPerForge; attempt++)
        {
            // Round-robin families so the pack has theme variety and the search
            // cannot get stuck on a single sparse bank.
            var family = WordList.Families[(id * 7 + attempt) % families];
            var rng = new Random(unchecked(id * 1_000_003 ^ (attempt + 1) * 9176));

            var candidates = trie.EnumerateFromLetters(family.Letters)
                .Where(w => w.Length >= ForgeSession.MinWordLength && !blocklist.Contains(w))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(w => w, StringComparer.Ordinal)
                .ToList();
            if (candidates.Count < targetWords + 2) continue;

            // A keystone must be UNIQUELY the longest solution word, and there
            // must still be >= 2 words left in the jar for the independent solver.
            int maxLen = candidates.Max(w => w.Length);
            var keystonePool = candidates
                .Where(w => w.Length == maxLen || w.Length == maxLen - 1)
                .Where(w => candidates.Count(x => x.Length < w.Length) >= targetWords + 1)
                .OrderBy(_ => rng.Next())
                .ToList();
            if (keystonePool.Count == 0) continue;

            // Prefer a rare keystone occasionally, but only when the DI budget allows.
            string keystone = keystonePool.FirstOrDefault(w => !common.Contains(w) && rng.NextDouble() < 0.35)
                              ?? keystonePool[rng.Next(keystonePool.Count)];

            var shorter = candidates.Where(w => w.Length < keystone.Length && w != keystone).ToList();
            if (shorter.Count < targetWords - 1) continue;

            var chosen = shorter.OrderBy(_ => rng.Next()).Take(targetWords - 1).ToList();
            if (chosen.Count(x => x.Length <= 4) < cfg.Content.MinShortWordsLengthLTE4) continue;

            // Shorter words first, keystone always LAST (ForgeSession enforces this).
            var solutionWords = new List<string>(chosen) { keystone };

            var draftForge = new ForgeData
            {
                ForgeId = id,
                Theme = family.Theme,
                LetterBank = family.Letters,
                SolutionWords = solutionWords,
                KeystoneWord = keystone,
                DifficultyIndex = ForgeValidator.DifficultyFromContent(
                    new ForgeData { ForgeId = id, LetterBank = family.Letters, SolutionWords = solutionWords },
                    cfg.Weights, common, shape.IsBreather),
                ParTimeSeconds = 0,
                Seed = unchecked(id * 7919 ^ attempt),
                Slots = new List<GridSlot>()
            };
            if (band is not null && !band.Contains(draftForge.DifficultyIndex)) continue;

            List<GridSlot>? slots = BuildGrid(solutionWords, keystone, cfg.Content.MinGridIntersections, rng);
            if (slots is null) continue;

            draftForge.Slots = slots;
            draftForge.ParTimeSeconds = DifficultyModel.ParTimeSeconds(draftForge.DifficultyIndex, cfg.ParTime);
            draftForge.Checksum = draftForge.ComputeChecksum();

            if (ForgeValidator.Validate(draftForge, trie, cfg, blocklist, common).Count == 0)
                return draftForge;
        }
        return null;
    }

    // ------------------------------------------------------------ grid layout
    private sealed record Placement(int Row, int Column, bool Vertical, int Score);

    private static List<GridSlot>? BuildGrid(List<string> words, string keystone,
                                             int minIntersections, Random rng)
    {
        var occupied = new Dictionary<(int Row, int Column), List<(char Letter, bool Vertical)>>();
        var placed = new List<(string Word, int Row, int Column, bool Vertical, int Score)>();

        void Place(string word, int row, int col, bool vertical, int score)
        {
            placed.Add((word, row, col, vertical, score));
            for (int i = 0; i < word.Length; i++)
            {
                var cell = vertical ? (row + i, col) : (row, col + i);
                if (!occupied.TryGetValue(cell, out var letters))
                {
                    letters = new List<(char, bool)>(2);
                    occupied[cell] = letters;
                }
                letters.Add((word[i], vertical));
            }
        }

        Place(keystone, 0, 0, false, 0);

        var others = words.Where(w => w != keystone).OrderBy(_ => rng.Next()).ToList();
        foreach (string word in others)
        {
            Placement? best = FindBestPlacement(word, occupied, rng);
            if (best is not null)
            {
                Place(word, best.Row, best.Column, best.Vertical, best.Score);
            }
            else
            {
                int nextRow = occupied.Count == 0 ? 0 : occupied.Keys.Max(k => k.Row) + 2;
                Place(word, nextRow, 0, false, 0);
            }
        }

        // Normalise to a zero-based, non-negative grid.
        int minRow = placed.Min(p => p.Row);
        int minCol = placed.Min(p => p.Column);
        var slots = placed.Select(p => new GridSlot
        {
            Word = p.Word,
            Row = p.Row - minRow,
            Column = p.Column - minCol,
            Direction = p.Vertical ? "v" : "h",
            IsKeystone = p.Word == keystone
        }).ToList();

        var probe = new ForgeData { Slots = slots };
        return probe.IntersectionCount() >= minIntersections ? slots : null;
    }

    private static Placement? FindBestPlacement(
        string word,
        Dictionary<(int Row, int Column), List<(char Letter, bool Vertical)>> occupied,
        Random rng)
    {
        Placement? best = null;
        var directions = new[] { false, true }.OrderBy(_ => rng.Next()).ToArray();

        foreach (bool vertical in directions)
        {
            for (int k = 0; k < word.Length; k++)
            {
                foreach (var kv in occupied)
                {
                    if (!kv.Value.Any(x => x.Letter == word[k])) continue;

                    int row = vertical ? kv.Key.Row - k : kv.Key.Row;
                    int col = vertical ? kv.Key.Column : kv.Key.Column - k;

                    int perpendicular = 0, parallel = 0;
                    bool conflict = false;
                    for (int i = 0; i < word.Length; i++)
                    {
                        var cell = vertical ? (row + i, col) : (row, col + i);
                        if (!occupied.TryGetValue(cell, out var letters)) continue;
                        bool matched = false;
                        foreach (var (letter, otherVertical) in letters)
                        {
                            if (letter != word[i]) { conflict = true; break; }
                            matched = true;
                            if (otherVertical != vertical) perpendicular++;
                            else parallel++;
                        }
                        if (conflict) break;
                        _ = matched;
                    }
                    if (conflict || parallel > 0 || perpendicular <= 0) continue;

                    var candidate = new Placement(row, col, vertical, perpendicular);
                    if (best is null || candidate.Score > best.Score ||
                        (candidate.Score == best.Score && rng.NextDouble() < 0.3))
                        best = candidate;
                }
            }
        }
        return best;
    }

    private static string LocateRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "design", "balance_config.json"))) return dir.FullName;
        throw new DirectoryNotFoundException("Pass the LexiForge repo root as the first argument.");
    }
}
