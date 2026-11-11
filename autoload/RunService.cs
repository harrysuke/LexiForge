using System;
using System.Linq;
using Godot;
using LexiForge.Core;

namespace LexiForge.Autoload;

/// <summary>
/// Godot signal adapter over the pure-C# RunState. There is intentionally NO
/// gameplay maths here: Heat, rune effects, draft sequencing and run-end
/// payloads are all computed in core/RunState.cs and verified in CoreTests.
/// An earlier sketch reimplemented HeatModel inside this autoload, which meant
/// the shipped game and the 10k-player balance simulator could silently drift.
/// </summary>
public sealed partial class RunService : Node
{
    public static RunService Instance { get; private set; } = null!;

    private RunState _state = null!;

    public double Heat => _state?.Heat ?? 0;
    public int ForgesInRun => _state?.ForgesInRun ?? 0;
    public int RunScore => _state?.RunScore ?? 0;
    public bool IsRunOver => _state?.IsRunOver ?? false;
    public bool IsDraftForge => _state?.IsDraftForge ?? false;
    public bool IsFirstRunEver => _state?.FirstRunEver ?? false;
    public bool CanRerollFree => _state?.CanRerollFree ?? false;
    public RuneEffects Effects => _state?.Effects ?? RuneEffects.None;
    public System.Collections.Generic.IReadOnlyList<RuneData> OwnedRunes
        => _state?.OwnedRunes ?? Array.Empty<RuneData>();

    public override void _Ready()
    {
        Instance = this;
        _state = new RunState(ConfigService.Instance.Config);
    }

    public void StartRun(int seed, bool isFirstRunEver)
    {
        _state.StartRun(seed, isFirstRunEver);
        AdManager.Instance.SetInRun(true);
        EventBus.Instance.EmitSignal(EventBus.SignalName.HeatChanged, 0);
        AnalyticsService.Instance.Record("run_start", seed.ToString());
    }

    /// <summary>Returns the run-end payload only when the run actually ends.</summary>
    public RunEndInfo? ApplyForge(ForgeOutcome outcome, double difficultyIndex)
    {
        RunEndInfo? end = _state.ApplyForge(outcome, difficultyIndex);
        EventBus.Instance.EmitSignal(EventBus.SignalName.HeatChanged, (int)Math.Round(_state.Heat));
        AnalyticsService.Instance.Record("forge_end", outcome.ForgeId.ToString(),
                                         outcome.TotalCoins.ToString(), ((int)Math.Round(_state.Heat)).ToString());

        if (end is not null)
        {
            // Guild rune: a genuinely FREE Mark at run end (GDD 3.4). Granted
            // here, once per run ending, never from an ad callback.
            if (end.FreeMarks > 0) EconomyService.Instance.AddMarks(end.FreeMarks, "guild_rune");
            AdManager.Instance.SetInRun(false);
            string runes = string.Join(",", end.Runes);
            EventBus.Instance.EmitSignal(EventBus.SignalName.RunEnded, end.TotalScore, end.Forges, runes);
            AnalyticsService.Instance.Record("run_end", end.TotalScore.ToString(), end.Forges.ToString());
        }
        else if (_state.IsDraftForge)
        {
            var offer = _state.DraftOffer();
            EventBus.Instance.EmitSignal(EventBus.SignalName.DraftOffered,
                                         string.Join(",", offer.Select(r => r.Id)));
        }
        return end;
    }

    public System.Collections.Generic.IReadOnlyList<RuneData> DraftOffer() => _state.DraftOffer();

    public System.Collections.Generic.IReadOnlyList<RuneData> RerollOffer()
    {
        int coinCost = ConfigService.Instance.Config.Sinks.RuneDraftRerollCoins;
        // Charge BEFORE generating: a player who cannot pay must keep both the
        // original offer and the original balance. Mirror's free reroll skips
        // both currency sinks and is consumed inside RunState.RerollOffer().
        if (!_state.CanRerollFree &&
            !EconomyService.Instance.TrySpendMarks(1, "draft_reroll") &&
            !EconomyService.Instance.TrySpendCoins(coinCost, "draft_reroll"))
            return _state.DraftOffer();

        AnalyticsService.Instance.Record("draft_reroll", _state.CanRerollFree ? "free" : "paid");
        return _state.RerollOffer();
    }

    public void Draft(RuneData rune)
    {
        _state.Draft(rune);
        var save = SaveService.Instance.Data;
        if (!save.DiscoveredRunes.Contains(rune.Id)) save.DiscoveredRunes.Add(rune.Id);
        SaveService.Instance.MarkDirty();
        AnalyticsService.Instance.Record("rune_drafted", rune.Id);
    }

    public void CoolForge()
    {
        _state.CoolForge();
        EventBus.Instance.EmitSignal(EventBus.SignalName.HeatChanged, (int)Math.Round(_state.Heat));
    }

    public int RunSeed => _state.RunSeed;
}
