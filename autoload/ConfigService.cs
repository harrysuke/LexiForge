using System;
using Godot;
using LexiForge.Ads;
using LexiForge.Core;
// .NET 8 also has a System.IO.FileAccess enum; be explicit about Godot's type.
using FileAccess = Godot.FileAccess;

namespace LexiForge.Autoload;

/// <summary>
/// Thin Godot adapter over the pure-C# BalanceConfig. This file does ONE thing:
/// read res://data/balance_config.json and hand the text to the exact same
/// parser that LevelGen, BalanceSim and CoreTests use.
///
/// The previous sketch only printed the version string and silently used
/// compiled defaults, so every tuning change in the JSON was a no-op at runtime.
/// A malformed config now fails loudly at boot instead of shipping a half-tuned
/// economy. A signed remote-config payload may override selected levers later,
/// but startup must NEVER block on a network fetch.
/// </summary>
public sealed partial class ConfigService : Node
{
    private const string ConfigPath = "res://data/balance_config.json";

    public static ConfigService Instance { get; private set; } = null!;

    public BalanceConfig Config { get; private set; } = new();
    public string ConfigVersion => Config.ConfigVersion;
    public DifficultyShapeTuning Shape => Config.Shape;
    public DifficultyWeights Weights => Config.Weights;
    public ParTimeTuning ParTime => Config.ParTime;
    public EconomyTuning Economy => Config.Economy;
    public HeatTuning Heat => Config.Heat;
    public AdPolicyConfig AdPolicy => Config.AdPolicy;
    public System.Collections.Generic.Dictionary<string, AdPlacementRule> Placements => Config.Placements;

    public override void _Ready()
    {
        Instance = this;

        if (!FileAccess.FileExists(ConfigPath))
        {
            GD.PushError($"[Config] {ConfigPath} is missing. Ship design/balance_config.json into res://data.");
            return;
        }

        try
        {
            using var file = FileAccess.Open(ConfigPath, FileAccess.ModeFlags.Read);
            Config = BalanceConfig.FromJson(file.GetAsText());
            GD.Print($"[Config] {ConfigVersion} | runes={Config.Runes.Count} placements={Config.Placements.Count} bands={Config.PackBands.Count}");
            foreach (string diagnostic in Config.Diagnostics)
                GD.PushWarning($"[Config] {diagnostic}");
        }
        catch (Exception ex)
        {
            // Do NOT swallow this as "use defaults": a partially parsed economy
            // would silently diverge from the balance simulator.
            GD.PushError($"[Config] balance_config.json is invalid: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Optional post-boot override from a signed remote-config payload.
    /// Only the compliance kill switch is supported for now: a value of "true"
    /// DISABLES interstitials everywhere without touching opt-in rewarded ads.
    /// Named for what it DOES at the call site; AdPolicyConfig exposes the
    /// positively-named InterstitialEnabled so no code ever inverts a boolean
    /// by accident.
    /// </summary>
    public void ApplyRemoteOverrides(System.Collections.Generic.IReadOnlyDictionary<string, string> values)
    {
        if (values.TryGetValue("interstitialKillSwitch", out string? raw) &&
            bool.TryParse(raw, out bool kill) && kill)
        {
            Config.SetInterstitialEnabled(false);
            GD.Print("[Config] remote kill switch applied: interstitials disabled, rewarded ads unaffected.");
        }
    }
}
