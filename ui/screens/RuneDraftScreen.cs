using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Rune draft after forges 1, 3 and 5 (config: heat.draftsAfterForges).
/// Three pooled cards are rebuilt in place — no scene reload, no QueueFree.
/// Rarity/stacking rules live in core/RuneDraft.cs; this screen only renders
/// the offer and reports the choice back to RunService.
/// </summary>
public sealed partial class RuneDraftScreen : Control, IScreen
{
    private readonly Button[] _cards = new Button[3];
    private Label _header = null!;
    private Label _owned = null!;
    private Label _status = null!;
    private Button _reroll = null!;
    private PauseOverlay _pause = null!;
    private IReadOnlyList<RuneData> _offer = Array.Empty<RuneData>();
    private int _runIndex;

    public override void _Ready()
    {
        var background = new ColorRect { Color = Ui.Bg, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 22, 56);
        column.AddChild(Ui.MakeLabel("DRAFT A RUNE", 58, Ui.Gold));
        _header = Ui.MakeLabel("", 27, Ui.Muted);
        column.AddChild(_header);

        for (int i = 0; i < _cards.Length; i++)
        {
            int index = i; // captured once; the buttons are pooled for the session
            // tapCue null: Pick() emits rune.pick with the rune's family index,
            // which selects the timbre (steel / bronze / slate) in the catalogue.
            var card = Ui.MakeButton("", () => Pick(index), Ui.PanelRaised, 200, 30, null);
            card.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            column.AddChild(card);
            _cards[i] = card;
        }

        _reroll = Ui.MakeButton("Reroll offer", OnReroll, Ui.PanelRaised, 96, 28, null);
        column.AddChild(_reroll);
        _status = Ui.MakeLabel("", 26, Ui.Ember);
        column.AddChild(_status);
        _owned = Ui.MakeLabel("", 24, Ui.Muted);
        column.AddChild(_owned);

        _pause = PauseOverlay.AttachTo(this, "Leave run", LeaveRun);
    }

    public void OnEnter(object? payload)
    {
        _runIndex = payload is DraftPayload draft ? draft.RunIndex : 0;
        _status.Text = "";
        _pause.Close();
        Render(RunService.Instance.DraftOffer());
    }

    public void OnExit() => _pause.Close();
    public void OnBack() => _pause.Toggle();

    private void Render(IReadOnlyList<RuneData> offer)
    {
        _offer = offer;
        _header.Text = $"Forge {_runIndex + 1} complete  ·  Heat {RunService.Instance.Heat:0}/100  ·  " +
                       "Runes last for this run and stack";
        _status.Text = "";

        for (int i = 0; i < _cards.Length; i++)
        {
            Button card = _cards[i];
            bool hasRune = i < offer.Count && offer[i] is not null;
            card.Visible = hasRune;
            if (!hasRune) continue;

            RuneData rune = offer[i];
            card.Text = $"{rune.Name}  ·  {rune.Kind}  ·  {rune.Rarity}\n{rune.Effect}";
            Color colour = rune.Kind switch
            {
                RuneKind.Score => Ui.Ember,
                RuneKind.Economy => Ui.Good,
                _ => Ui.Gold
            };
            card.AddThemeStyleboxOverride("normal", Ui.Box(Ui.PanelRaised));
            card.AddThemeStyleboxOverride("hover", Ui.Box(colour));
            card.AddThemeColorOverride("font_color", colour);
            card.Disabled = false;
        }

        int cost = ConfigService.Instance.Config.Sinks.RuneDraftRerollCoins;
        _reroll.Text = RunService.Instance.CanRerollFree
            ? "Reroll offer (Mirror: free)"
            : $"Reroll offer (1 Mark or {cost} coins)";

        var owned = RunService.Instance.OwnedRunes;
        _owned.Text = owned.Count == 0 ? "No runes drafted yet" : "Runes: " + string.Join(", ", Names(owned));
    }

    private static List<string> Names(IReadOnlyList<RuneData> runes)
    {
        var names = new List<string>(runes.Count);
        foreach (RuneData rune in runes) names.Add(rune.Name);
        return names;
    }

    private void OnReroll()
    {
        var cfg = ConfigService.Instance.Config;
        bool canAfford = RunService.Instance.CanRerollFree ||
                         EconomyService.Instance.Marks >= 1 ||
                         EconomyService.Instance.Coins >= cfg.Sinks.RuneDraftRerollCoins;
        if (!canAfford)
        {
            EventBus.Cue("ui.disabled");
            _status.Text = $"Need 1 Mark or {cfg.Sinks.RuneDraftRerollCoins} coins. Pick a rune to continue.";
            return;
        }
        EventBus.Cue("rune.reroll");
        Render(RunService.Instance.RerollOffer());
    }

    private void Pick(int index)
    {
        if (index < 0 || index >= _offer.Count) return;
        RuneData rune = _offer[index];
        // Magnitude = rune family, not a random index: the catalogue maps 0/1/2
        // onto steel (Score), bronze (Economy) and slate (Utility).
        EventBus.Cue("rune.pick", FamilyIndex(rune.Kind));
        RunService.Instance.Draft(rune);
        AnalyticsService.Instance.Record("rune_pick", rune.Id);

        int next = _runIndex + 1;
        ForgeData? forge = ProgressionService.Instance.ForgeFor(next);
        if (forge is null) { EventBus.Cue("ui.disabled"); SceneRouter.Instance.GoHome(); return; }
        SceneRouter.Instance.GoToForge(forge, next, RunService.Instance.Effects);
    }

    /// <summary>Stable family order used by the catalogue's byMagnitude variation.</summary>
    private static int FamilyIndex(RuneKind kind) => kind switch
    {
        RuneKind.Score => 0,
        RuneKind.Economy => 1,
        _ => 2 // Utility
    };

    private void LeaveRun()
    {
        AdManager.Instance.SetAbandoned(true);
        AdManager.Instance.SetInRun(false);
        SceneRouter.Instance.GoHome();
    }
}
