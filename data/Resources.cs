using System;
using Godot;

namespace LexiForge.Data;

/// <summary>Godot-facing data types. Game logic stays in LexiForge.Core (no Godot dependency).</summary>

[GlobalClass]
public partial class ForgeDefinition : Resource
{
    [Export] public int ForgeId { get; set; }
    [Export] public string Theme { get; set; } = "";
    [Export] public string LetterBank { get; set; } = "";
    [Export] public string[] SolutionWords { get; set; } = Array.Empty<string>();
    [Export] public string KeystoneWord { get; set; } = "";
    [Export] public float DifficultyIndex { get; set; }
    [Export] public float ParTimeSeconds { get; set; }
    [Export] public int Seed { get; set; }
    [Export] public string Checksum { get; set; } = "";
    [Export] public int[] SlotLengths { get; set; } = Array.Empty<int>();
}

[GlobalClass]
public partial class PackDefinition : Resource
{
    [Export] public int PackId { get; set; }
    [Export] public string Theme { get; set; } = "";
    [Export] public Godot.Collections.Array<ForgeDefinition> Forges { get; set; } = new();
    [Export] public Vector2 DifficultyBand { get; set; }
}

public enum RuneType { Score, Utility, Economy }
public enum RuneRarity { Common, Uncommon, Rare }

[GlobalClass]
public partial class RuneDefinition : Resource
{
    [Export] public string RuneId { get; set; } = "";
    [Export] public string DisplayName { get; set; } = "";
    [Export] public RuneType Type { get; set; }
    [Export] public RuneRarity Rarity { get; set; }
    [Export] public float CoinDeltaPercent { get; set; }
    [Export] public float DifficultyDelta { get; set; }
    [Export(PropertyHint.MultilineText)] public string EffectText { get; set; } = "";
}

/// <summary>Payload emitted when a forge finishes. Views listen; services never reference views.</summary>
public sealed class ForgeCompletedArgs : EventArgs
{
    public required int ForgeId { get; init; }
    public required int Coins { get; init; }
    public required int Stars { get; init; }
    public required double SolveSeconds { get; init; }
    public required double ParSeconds { get; init; }
    public required int HintsUsed { get; init; }
    public required int WrongAttempts { get; init; }
    public required double FlowPeak { get; init; }
}
