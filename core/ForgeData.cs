using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexiForge.Core;

/// <summary>
/// Pure C# level data. NO Godot references: this type is shared by the shipped game,
/// the offline LevelGen tool, the CI solver and the verification harness.
///
/// Forges are generated offline and shipped as read-only JSON under res://data/forges.
/// The checksum is verified on load so a corrupted/truncated catalogue fails loudly
/// instead of producing an unsolvable puzzle on a player's device.
/// </summary>
public sealed class GridSlot
{
    /// <summary>Lower-case solution word occupying this slot.</summary>
    public string Word { get; set; } = "";

    /// <summary>Zero-based grid row of the first letter.</summary>
    public int Row { get; set; }

    /// <summary>Zero-based grid column of the first letter.</summary>
    public int Column { get; set; }

    /// <summary>"h" or "v" — kept as a string so the JSON stays human-readable.</summary>
    public string Direction { get; set; } = "h";

    /// <summary>True only for the single keystone slot, which unlocks last.</summary>
    public bool IsKeystone { get; set; }

    [JsonIgnore]
    public bool IsVertical => Direction == "v";

    /// <summary>Enumerates (row, column, letter) cells covered by this slot.</summary>
    public IEnumerable<(int Row, int Column, char Letter)> Cells()
    {
        for (int i = 0; i < Word.Length; i++)
        {
            yield return IsVertical
                ? (Row + i, Column, Word[i])
                : (Row, Column + i, Word[i]);
        }
    }
}

public sealed class ForgeData
{
    public int ForgeId { get; set; }
    public string Theme { get; set; } = "";
    public string LetterBank { get; set; } = "";

    /// <summary>
    /// Solution words in reveal order. The keystone is ALWAYS the final element.
    /// ForgeSession refuses to accept it until every other slot is filled.
    /// </summary>
    public List<string> SolutionWords { get; set; } = new();

    public string KeystoneWord { get; set; } = "";
    public List<GridSlot> Slots { get; set; } = new();
    public double DifficultyIndex { get; set; }
    public double ParTimeSeconds { get; set; }
    public int Seed { get; set; }
    public string Checksum { get; set; } = "";

    /// <summary>Count of cells shared by two or more slots (visual coherence, GDD 3.2).</summary>
    public int IntersectionCount()
    {
        var occupied = new Dictionary<(int, int), List<char>>();
        foreach (var slot in Slots)
        {
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
        return occupied.Count(kv => kv.Value.Count > 1 && kv.Value.Distinct().Count() == 1);
    }

    public string ComputeChecksum()
    {
        string canonical = string.Join("|",
            ForgeId.ToString(CultureInfo.InvariantCulture),
            Theme,
            LetterBank,
            string.Join(",", SolutionWords),
            KeystoneWord,
            string.Join(";", Slots.Select(s => $"{s.Word}:{s.Row}:{s.Column}:{s.Direction}:{(s.IsKeystone ? 1 : 0)}")),
            Seed.ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).Substring(0, 16).ToLowerInvariant();
    }

    public bool HasValidChecksum()
        => Checksum.Length == 16 && string.Equals(Checksum, ComputeChecksum(), StringComparison.OrdinalIgnoreCase);
}

public sealed class ForgePack
{
    public int PackId { get; set; }
    public string Theme { get; set; } = "";
    public double[] DifficultyBand { get; set; } = { 5.0, 95.0 };
    public List<ForgeData> Forges { get; set; } = new();
}

/// <summary>Read-only catalogue loader. Never generates levels on-device (GDD 6.3).</summary>
public static class ForgeCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ForgePack LoadPack(string path)
        => LoadPackFromJson(File.ReadAllText(path));

    /// <summary>
    /// Parses one pack from its JSON text. The Godot runtime uses this overload:
    /// res:// files are packed inside the APK/PCK and are read with FileAccess,
    /// not through System.IO. Both paths share the same validation.
    /// </summary>
    public static ForgePack LoadPackFromJson(string json)
    {
        var pack = JsonSerializer.Deserialize<ForgePack>(json, Options)
                   ?? throw new InvalidDataException("Empty forge pack");
        if (pack.Forges.Count == 0) throw new InvalidDataException("No forges in pack");
        foreach (var forge in pack.Forges)
            if (!forge.HasValidChecksum())
                throw new InvalidDataException(
                    $"Forge {forge.ForgeId} failed its checksum — corrupted or truncated level data.");
        return pack;
    }

    /// <summary>Loads every pack_XX.json in a folder, ordered by pack id then forge id.</summary>
    public static List<ForgeData> LoadAll(string directory)
    {
        var result = new List<ForgeData>();
        foreach (string path in Directory.GetFiles(directory, "pack_*.json").OrderBy(p => p, StringComparer.Ordinal))
            result.AddRange(LoadPack(path).Forges.OrderBy(f => f.ForgeId));
        return result;
    }

    /// <summary>Combines pack JSON texts (already read from res://) into one ordered catalogue.</summary>
    public static List<ForgeData> LoadAllFromJson(IEnumerable<string> packJsons)
    {
        var packs = new List<ForgePack>();
        foreach (string json in packJsons) packs.Add(LoadPackFromJson(json));
        return packs.OrderBy(p => p.PackId)
                    .SelectMany(p => p.Forges.OrderBy(f => f.ForgeId))
                    .ToList();
    }

    public static string ToJson(ForgePack pack) => JsonSerializer.Serialize(pack, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    });
}
