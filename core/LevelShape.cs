using System;

namespace LexiForge.Core;

/// <summary>
/// Pure C# — NO Godot references. This file is linked into the Godot project,
/// the offline level generator and the headless balance simulator.
/// Keeping formulas here guarantees the simulator and the shipped game agree.
/// </summary>

public readonly record struct LevelShape(
    int ForgeIndex,
    int WordCount,
    int BankSize,
    double AverageWordLength,
    double Rarity,
    bool IsBreather);

public sealed class DifficultyShapeTuning
{
    public double DecayForgeConstant { get; init; } = 110.0;
    public int WordBase { get; init; } = 3;
    public double WordScale { get; init; } = 9.0;
    public int BankBase { get; init; } = 3;
    public double BankScale { get; init; } = 3.0;
    public double BankExponent { get; init; } = 0.8;
    public double AvgLenBase { get; init; } = 3.3;
    public double AvgLenScale { get; init; } = 2.5;
    public double RarityBase { get; init; } = 0.02;
    public double RarityScale { get; init; } = 0.28;
    public int BreatherEveryN { get; init; } = 5;
    public int BreatherWordPenalty { get; init; } = 1;
    public double BreatherRarityMultiplier { get; init; } = 0.6;
}

public sealed class DifficultyWeights
{
    public double PerExtraWord { get; init; } = 4.0;
    public double PerExtraBankLetter { get; init; } = 5.0;
    public double PerExtraAvgLength { get; init; } = 4.5;
    public double Rarity { get; init; } = 18.0;
    public double BreatherBonus { get; init; } = -8.0;
    public double MinDI { get; init; } = 5.0;
    public double MaxDI { get; init; } = 95.0;
}

public sealed class ParTimeTuning
{
    public double Base { get; init; } = 14.0;
    public double Linear { get; init; } = 1.15;
    public double Quadratic { get; init; } = 0.004;
    public double CapSeconds { get; init; } = 120.0;
}

public static class DifficultyModel
{
    /// <summary>Deterministic content shape for a 1-based forge index.</summary>
    public static LevelShape ShapeFor(int forgeIndex, DifficultyShapeTuning t)
    {
        forgeIndex = Math.Max(1, forgeIndex);
        double progress = 1.0 - Math.Exp(-forgeIndex / t.DecayForgeConstant);

        int words = (int)Math.Round(t.WordBase + t.WordScale * progress);
        int bank = (int)Math.Round(t.BankBase + t.BankScale * Math.Pow(progress, t.BankExponent));
        double avgLen = t.AvgLenBase + t.AvgLenScale * progress;
        double rarity = t.RarityBase + t.RarityScale * progress;

        bool breather = forgeIndex % Math.Max(1, t.BreatherEveryN) == 0;
        if (breather)
        {
            words = Math.Max(3, words - t.BreatherWordPenalty);
            rarity *= t.BreatherRarityMultiplier;
        }

        return new LevelShape(forgeIndex, words, bank, avgLen, rarity, breather);
    }

    /// <summary>Difficulty Index, 5..95. Content-driven so generated forges can be validated against a target band.</summary>
    public static double Index(LevelShape s, DifficultyWeights w)
    {
        double raw = w.PerExtraWord * (s.WordCount - 3)
                   + w.PerExtraBankLetter * (s.BankSize - 3)
                   + w.PerExtraAvgLength * (s.AverageWordLength - 3.0)
                   + w.Rarity * s.Rarity
                   + (s.IsBreather ? w.BreatherBonus : 0.0);
        return Math.Clamp(raw, w.MinDI, w.MaxDI);
    }

    public static double ParTimeSeconds(double di, ParTimeTuning t)
        => Math.Min(t.CapSeconds, t.Base + t.Linear * di + t.Quadratic * di * di);
}

public sealed class EconomyTuning
{
    public int Base { get; init; } = 20;
    public int PerSolutionWord { get; init; } = 6;
    public int PerBonusWord { get; init; } = 3;
    public int BonusWordCap { get; init; } = 8;
    public double FlowStep { get; init; } = 0.15;
    public double FlowCap { get; init; } = 2.5;
    public int HintCostBase { get; init; } = 90;
    public int HintCostStep { get; init; } = 60;
    public int MaxHintsPerForge { get; init; } = 3;
    public int StarBonus1 { get; init; } = 0;
    public int StarBonus2 { get; init; } = 15;
    public int StarBonus3 { get; init; } = 40;
}

public readonly record struct ForgeResult(
    int Coins, int Stars, double FlowPeak, int BonusWords, int HintsUsed,
    double SolveSeconds, double ParSeconds, int WrongAttempts, bool Perfect);

public static class EconomyModel
{
    public static double FlowFor(int cleanStreak, EconomyTuning t)
        => 1.0 + Math.Min(t.FlowCap - 1.0, Math.Max(0, cleanStreak) * t.FlowStep);

    /// <param name="runeDelta">Bounded build modifier, e.g. +0.18 for a three-rune build.</param>
    public static int LevelCoins(int wordCount, int bonusWords, double flow, double runeDelta, EconomyTuning t)
    {
        int bonus = Math.Min(Math.Max(0, bonusWords), t.BonusWordCap) * t.PerBonusWord;
        double raw = (t.Base + t.PerSolutionWord * wordCount + bonus) * Math.Clamp(flow, 1.0, t.FlowCap);
        return Math.Max(0, (int)Math.Round(raw * (1.0 + runeDelta)));
    }

    /// <param name="hintNumber">1-based hint index within this forge: 90 / 150 / 210 by default.</param>
    public static int HintCost(int hintNumber, EconomyTuning t)
        => t.HintCostBase + t.HintCostStep * Math.Max(0, hintNumber - 1);

    public static int StarBonus(int stars, EconomyTuning t) => stars switch
    {
        3 => t.StarBonus3,
        2 => t.StarBonus2,
        _ => t.StarBonus1
    };

    public static int Stars(int hints, int wrongAttempts, double seconds, double par, EconomyTuning t)
    {
        _ = t;
        if (hints == 0 && seconds <= par && wrongAttempts <= 2) return 3;
        if (hints <= 1 && seconds <= par * 1.5) return 2;
        return 1;
    }
}

public sealed class HeatTuning
{
    public double GainBase { get; init; } = 8.0;
    public double GainPerDI { get; init; } = 0.20;
    public double GainPerParOverrun { get; init; } = 6.0;
    public double CoolOnPerfect { get; init; } = 5.0;
    public double Max { get; init; } = 100.0;
    public int MaxForgesPerRun { get; init; } = 8;
}

public static class HeatModel
{
    /// <summary>
    /// The forge heats up when the player struggles, not on a timer.
    /// A run ends on a reward screen, never a loss screen.
    /// </summary>
    public static double Next(double current, double di, double solveToParRatio, bool perfect, HeatTuning t)
    {
        double overrun = Math.Max(0.0, solveToParRatio - 1.0);
        double gain = t.GainBase
                    + t.GainPerDI * di
                    + t.GainPerParOverrun * Math.Min(overrun, 1.0)
                    - (perfect ? t.CoolOnPerfect : 0.0);
        return Math.Clamp(current + gain, 0.0, t.Max);
    }
}
