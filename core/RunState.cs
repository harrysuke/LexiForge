using System;
using System.Collections.Generic;
using System.Linq;

namespace LexiForge.Core;

public sealed class RunEndInfo
{
    public required int TotalScore { get; init; }
    public required int Forges { get; init; }
    public required double FinalHeat { get; init; }
    public required int FreeMarks { get; init; }
    public required IReadOnlyList<string> Runes { get; init; }

    /// <summary>
    /// Words solved across the whole run. The run-end headline is a COUNT of what
    /// the player made, not a verdict (UI-53), and it must end on a gain.
    /// </summary>
    public int WordsForged { get; init; }
}

/// <summary>
/// Pure C# run-level state: Heat, rune drafts, run score and forge sequencing.
///
/// RunService (autoload) is only a Godot signal adapter over this class — gameplay
/// logic must not live in an Autoload, because autoloads cannot be instanced or
/// unit-tested in isolation.
/// </summary>
public sealed class RunState
{
    private readonly BalanceConfig _cfg;
    private readonly RuneDraft _draft;
    private readonly List<RuneData> _owned = new();
    private readonly List<ForgeOutcome> _forges = new();

    private int _runSeed;
    private int _draftCount;
    private bool _firstDraftEver = true;

    /// <summary>
    /// GDD 3.3 "Cool the forge" must let the player CONTINUE the same run, so a
    /// cooling extends the forge cap instead of silently ending the run again at
    /// the next ApplyForge. (_state.ForgesInRun is deliberately never rewound —
    /// it is the run score/history counter, not a countdown.)
    /// </summary>
    private int _forgeCap;

    public RunState(BalanceConfig cfg)
    {
        _cfg = cfg ?? throw new ArgumentNullException(nameof(cfg));
        _draft = new RuneDraft(cfg.Runes, cfg.Draft);
    }

    public double Heat { get; private set; }
    public int ForgesInRun { get; private set; }
    public int RunScore { get; private set; }
    public int FreeRerollsRemaining { get; private set; }
    public IReadOnlyList<RuneData> OwnedRunes => _owned;
    public IReadOnlyList<ForgeOutcome> ForgeHistory => _forges;
    public RuneEffects Effects { get; private set; } = RuneEffects.None;
    public int RunSeed => _runSeed;

    /// <summary>Stays true for the whole tutorial run (unlike _firstDraftEver, which is consumed by the first draft).</summary>
    public bool FirstRunEver { get; private set; }

    public void StartRun(int seed, bool isFirstRunEver)
    {
        _runSeed = seed;
        _firstDraftEver = isFirstRunEver;
        FirstRunEver = isFirstRunEver;
        _owned.Clear();
        _forges.Clear();
        _draftCount = 0;
        Heat = 0;
        ForgesInRun = 0;
        RunScore = 0;
        Effects = RuneEffects.None;
        FreeRerollsRemaining = 0;
        _rerollsAccounted = 0;
        _forgeCap = _cfg.Heat.MaxForgesPerRun;
    }

    public bool IsRunOver => Heat >= _cfg.Heat.Max || ForgesInRun >= _forgeCap;

    public bool IsDraftForge => !IsRunOver && _cfg.DraftsAfterForges.Contains(ForgesInRun);

    /// <summary>Applies a finished forge. Returns null until the run actually ends.</summary>
    public RunEndInfo? ApplyForge(ForgeOutcome outcome, double difficultyIndex)
    {
        ForgesInRun++;
        _forges.Add(outcome);
        RunScore += outcome.TotalCoins;

        double ratio = outcome.ParSeconds > 0 ? outcome.SolveSeconds / outcome.ParSeconds : 1.0;
        double next = HeatModel.Next(Heat, difficultyIndex, ratio, outcome.Perfect, _cfg.Heat);

        // Anchor (-3 Heat/forge) and Quill (+2 clean cools on a perfect forge) are
        // explicit, additive rune effects. They are intentionally NOT inside
        // HeatModel, which the balance simulator links verbatim.
        next -= Effects.HeatGainReduction;
        if (outcome.Perfect) next -= Effects.CleanCoolsOnPerfect;
        Heat = Math.Clamp(next, 0.0, _cfg.Heat.Max);

        return IsRunOver ? BuildRunEnd() : null;
    }

    public IReadOnlyList<RuneData> DraftOffer()
        => _draft.Offer(_runSeed, ForgesInRun, _firstDraftEver && _draftCount == 0,
                        _owned.Select(r => r.Id).ToArray());

    /// <summary>Reroll is free once with Mirror, otherwise costs 1 Mark / coins (charged by RunService).</summary>
    public bool CanRerollFree => FreeRerollsRemaining > 0;

    /// <summary>
    /// Produces a genuinely NEW offer. The caller (RunService) has already
    /// charged a free reroll, 1 Mark or coins. Earlier this method returned
    /// DraftOffer() whenever FreeRerollsRemaining was 0, which silently made a
    /// PAID reroll a no-op — the offer never changed but the currency was gone.
    /// </summary>
    public IReadOnlyList<RuneData> RerollOffer()
    {
        if (FreeRerollsRemaining > 0) FreeRerollsRemaining--;
        _draftCount++;
        return _draft.Offer(unchecked(_runSeed + 104729 * (_draftCount + 1)), ForgesInRun,
                            false, _owned.Select(r => r.Id).ToArray());
    }

    public void Draft(RuneData rune)
    {
        if (rune is null) throw new ArgumentNullException(nameof(rune));
        _owned.Add(rune);
        _draftCount++;
        _firstDraftEver = false;
        Effects = RuneEffects.From(_owned, _cfg.Draft);
        FreeRerollsRemaining += Math.Max(0, Effects.FreeDraftRerolls - _rerollsAccounted);
        _rerollsAccounted = Effects.FreeDraftRerolls;
    }

    private int _rerollsAccounted;

    public void CoolForge()
    {
        Heat = Math.Clamp(Heat + _cfg.CoolForgeHeatDelta, 0.0, _cfg.Heat.Max);
        _forgeCap += _cfg.Heat.MaxForgesPerRun;
    }

    public RunEndInfo BuildRunEnd() => new()
    {
        TotalScore = RunScore,
        Forges = ForgesInRun,
        FinalHeat = Heat,
        FreeMarks = Effects.FreeMarksAtRunEnd,
        Runes = _owned.Select(r => r.Id).ToArray(),
        WordsForged = _forges.Sum(f => f.SolutionWords)
    };
}
