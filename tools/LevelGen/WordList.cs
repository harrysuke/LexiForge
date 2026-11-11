using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace LexiForge.Tools;

public sealed record BankFamily(string Letters, string Theme, string Words);

/// <summary>
/// Curated seed dictionary for the P0 (20-forge) vertical slice.
///
/// The word data lives in design/seed_dictionary.json so the C# generator and
/// the Python local-build harness (used when Windows App Control blocks local
/// .NET binaries) read the SAME content. Designers edit JSON, not code.
///
/// Each family lists words provably constructible from that bank's distinct
/// letters. The shipped dictionary is the UNION of all families; cross-family
/// words naturally become Bonus Jar finds. For the full 400-forge catalogue swap
/// in the 60k frequency-ranked word list + 100 theme lists (GDD 6.3) — the
/// generator/validator pipeline is unchanged.
/// </summary>
public static class WordList
{
    private sealed class SeedDocument
    {
        public string SchemaVersion { get; set; } = "";
        public List<SeedFamily> Families { get; set; } = new();
        public List<string> Rare { get; set; } = new();
        public List<string> Blocklist { get; set; } = new();
    }

    private sealed class SeedFamily
    {
        public string Letters { get; set; } = "";
        public string Theme { get; set; } = "";
        public string Words { get; set; } = "";
    }

    private static readonly Lazy<SeedDocument> Document = new(LoadDocument);

    public static IReadOnlyList<BankFamily> Families => _families.Value;
    public static IReadOnlySet<string> Blocklist => _blocklist.Value;
    public static IReadOnlySet<string> All => _all.Value;
    public static IReadOnlySet<string> Common => _common.Value;

    private static readonly Lazy<IReadOnlyList<BankFamily>> _families = new(() =>
        Document.Value.Families
            .Where(f => f.Letters.Length is >= 3 and <= 7 && f.Letters.All(c => c is >= 'a' and <= 'z'))
            .Select(f => new BankFamily(f.Letters, f.Theme, f.Words))
            .ToArray());

    private static readonly Lazy<IReadOnlySet<string>> _blocklist = new(() =>
        new HashSet<string>(Document.Value.Blocklist, StringComparer.Ordinal));

    private static readonly Lazy<IReadOnlySet<string>> _all = new(() =>
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in Families)
            foreach (string word in family.Words.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (word.Length >= 3 && word.All(c => c is >= 'a' and <= 'z'))
                    set.Add(word);
        return set;
    });

    private static readonly Lazy<IReadOnlySet<string>> _common = new(() =>
    {
        var rare = new HashSet<string>(Document.Value.Rare, StringComparer.Ordinal);
        return new HashSet<string>(All.Where(w => !rare.Contains(w)), StringComparer.Ordinal);
    });

    public static WordTrieDefinition BuildTrie()
    {
        var trie = new LexiForge.Core.WordTrie();
        foreach (string word in All.OrderBy(w => w, StringComparer.Ordinal)) trie.Add(word);
        return new WordTrieDefinition(trie, All);
    }

    private static SeedDocument LoadDocument()
    {
        string path = Locate();
        var doc = JsonSerializer.Deserialize<SeedDocument>(File.ReadAllText(path),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                  ?? throw new InvalidDataException($"Empty seed dictionary: {path}");
        if (doc.Families.Count == 0) throw new InvalidDataException($"No families in {path}");
        return doc;
    }

    private static string Locate()
    {
        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (string root in roots)
        {
            var dir = new DirectoryInfo(root);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "design", "seed_dictionary.json");
                if (File.Exists(candidate)) return candidate;
            }
        }
        throw new FileNotFoundException("Cannot locate design/seed_dictionary.json");
    }
}

public sealed record WordTrieDefinition(LexiForge.Core.WordTrie Trie, IReadOnlySet<string> Words);
