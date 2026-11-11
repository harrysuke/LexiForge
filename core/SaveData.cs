using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexiForge.Core;

/// <summary>Daily ad counters. Reset when the local UTC day changes.</summary>
public sealed class AdDayState
{
    public int DayNumber { get; set; }
    public int RewardedToday { get; set; }
    public int InterstitialsToday { get; set; }
    public int AppOpensToday { get; set; }
    public int MarksToday { get; set; }
    public Dictionary<string, int> PlacementUses { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Versioned offline save model. Pure C# so migrations are testable without a
/// scene tree. SaveService owns the atomic user:// write; this class owns shape.
/// </summary>
public sealed class SaveData
{
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; set; } = CurrentSchema;
    public string ConfigVersion { get; set; } = "";
    public int Coins { get; set; } = 300;
    public int Marks { get; set; }
    public int LifetimeForges { get; set; }
    public int Sessions { get; set; }
    public int HighestForgeUnlocked { get; set; } = 1;
    public int CurrentStreak { get; set; }
    public int LastDailyForgeDay { get; set; } = -1;
    public int FreeStreakFreezesLeft { get; set; } = 1;
    public bool ForgePassOwned { get; set; }
    public bool ConsentResolved { get; set; }
    public string ConsentStatus { get; set; } = "unknown";
    public int ConsentPolicyVersion { get; set; } = 1;

    /// <summary>forgeId -> best star count (1..3).</summary>
    public Dictionary<int, int> StarsByForge { get; set; } = new();
    public List<string> DiscoveredRunes { get; set; } = new();
    public List<int> UnlockedAtlasScenes { get; set; } = new();
    public AdDayState AdDay { get; set; } = new();
    public long LastSavedUtcUnix { get; set; }

    public int TotalStars => StarsByForge.Values.Sum();

    public int DayNumberFor(long unixSeconds) => (int)(unixSeconds / 86_400L);

    /// <summary>Rolls daily counters over at local-UTC midnight. Idempotent per day.</summary>
    public bool RollDaily(long nowUnix)
    {
        int day = DayNumberFor(nowUnix);
        if (AdDay.DayNumber == day) return false;
        AdDay = new AdDayState { DayNumber = day };
        if (day % 7 == 0) FreeStreakFreezesLeft = 1; // one free streak freeze per week
        return true;
    }

    public void RecordForge(int forgeId, int stars)
    {
        LifetimeForges++;
        if (!StarsByForge.TryGetValue(forgeId, out int best) || stars > best)
            StarsByForge[forgeId] = stars;
        HighestForgeUnlocked = Math.Max(HighestForgeUnlocked, forgeId + 1);
    }

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string ToJson() => JsonSerializer.Serialize(this, WriteOptions);

    /// <summary>Tolerant load: unknown fields ignored, missing fields defaulted, v1 migrated.</summary>
    public static SaveData FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonElement r = doc.RootElement;
        int schema = GetInt(r, "schemaVersion", 1);

        // Deserialise what matches, then layer explicit migrations / defaults.
        var save = JsonSerializer.Deserialize<SaveData>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new SaveData();

        save.StarsByForge ??= new Dictionary<int, int>();
        save.DiscoveredRunes ??= new List<string>();
        save.UnlockedAtlasScenes ??= new List<int>();
        save.AdDay ??= new AdDayState();
        save.AdDay.PlacementUses ??= new Dictionary<string, int>(StringComparer.Ordinal);

        if (schema < 2)
        {
            // v1 stored "coins" / "marks" only; everything else is additive.
            save.ConfigVersion = "";
            save.ConsentResolved = false;
            save.ForgePassOwned = false;
        }
        save.SchemaVersion = CurrentSchema;
        return save;
    }

    private static int GetInt(JsonElement e, string name, int fallback)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : fallback;

    public bool IsValid(out string reason)
    {
        if (Coins < 0 || Marks < 0) { reason = "negative currency"; return false; }
        if (LifetimeForges < 0 || Sessions < 0) { reason = "negative counters"; return false; }
        if (StarsByForge.Values.Any(s => s is < 0 or > 3)) { reason = "invalid star value"; return false; }
        reason = "ok";
        return true;
    }
}
