using System;
using System.Text;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Forge-complete card. Shown as an overlay on the pooled Forge scene (never a
/// separate scene load), so the next ResetFor can reuse every node.
///
/// The "Double the haul" button is strictly opt-in: it is hidden when AdPolicy
/// says the placement is not offerable, and ForgeScreen performs the credit only
/// after OnUserEarnedReward. There is always a plain Continue button with no ad.
/// </summary>
public sealed partial class ResultsCard : Control
{
    [Signal] public delegate void ContinueRequestedEventHandler();
    [Signal] public delegate void DoubleHaulRequestedEventHandler();

    private Label _stars = null!;
    private Label _coins = null!;
    private Label _detail = null!;
    private Label _bonusWords = null!;
    private Button _doubleButton = null!;
    private bool _doubled;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;

        var dim = new ColorRect { Color = new Color(0.02f, 0.015f, 0.01f, 0.82f) };
        AddChild(dim);
        Ui.FullRect(dim);

        var centre = new CenterContainer();
        AddChild(centre);
        Ui.FullRect(centre);

        var card = Ui.MakePanel(Ui.Panel, 28);
        card.CustomMinimumSize = new Vector2(880, 0);
        centre.AddChild(card);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 22);
        card.AddChild(column);

        column.AddChild(Ui.MakeLabel("FORGE COMPLETE", 46, Ui.Gold));
        _stars = Ui.MakeLabel("***", 64, Ui.Gold);
        column.AddChild(_stars);
        _coins = Ui.MakeLabel("+0 coins", 52, Ui.Ember);
        column.AddChild(_coins);
        _detail = Ui.MakeLabel("", 28, Ui.Text);
        column.AddChild(_detail);
        _bonusWords = Ui.MakeLabel("", 26, Ui.Muted);
        column.AddChild(_bonusWords);

        _doubleButton = Ui.MakeButton("Double the haul (opt-in ad)",
            () => EmitSignal(SignalName.DoubleHaulRequested), Ui.PanelRaised, 100, 28);
        column.AddChild(_doubleButton);

        column.AddChild(Ui.MakeButton("Continue", () => EmitSignal(SignalName.ContinueRequested), Ui.Ember, 112, 34));

        Visible = false;
    }

    public void Open(ForgeOutcome outcome, double heatBefore, double heatAfter, bool canDouble)
    {
        _doubled = false;
        _stars.Text = StarGlyphs(outcome.Result.Stars);
        _coins.Text = $"+{outcome.TotalCoins} coins";

        double delta = heatAfter - heatBefore;
        var sb = new StringBuilder();
        sb.Append($"Flow peak {outcome.Result.FlowPeak:0.00}x   ·   ");
        sb.Append($"{outcome.SolveSeconds:0}s / par {outcome.ParSeconds:0}s\n");
        sb.Append($"Stars bonus {outcome.StarCoins}   ·   Rune bonus {outcome.RuneBonusCoins}   ·   ");
        sb.Append($"Heat {(delta >= 0 ? "+" : "")}{delta:0}");
        _detail.Text = sb.ToString();

        _bonusWords.Text = outcome.BonusWords.Count == 0
            ? "Bonus Jar: —"
            : "Bonus Jar: " + string.Join("  ", outcome.BonusWords).ToUpperInvariant();

        _doubleButton.Visible = canDouble && outcome.TotalCoins > 0;
        _doubleButton.Disabled = false;
        _doubleButton.Text = "Double the haul (opt-in ad)";
        Visible = true;
        // The card is a gift, not a pop-up: a soft arrival, no fanfare.
        EventBus.Cue("ui.open");
    }

    /// <summary>Called by the screen only after the rewarded callback granted the bonus.</summary>
    /// <param name="totalCoins">The forge's coin total after doubling, for display only.</param>
    public void MarkDoubled(int totalCoins)
    {
        if (_doubled) return;
        _doubled = true;
        _coins.Text = $"+{totalCoins} coins (doubled)";
        _doubleButton.Disabled = true;
        _doubleButton.Text = "Haul doubled!";
    }

    /// <summary>Visible feedback on an ad no-fill; the overlay hides the HUD toast.</summary>
    public void NoteAdUnavailable()
    {
        _doubleButton.Disabled = false;
        _doubleButton.Text = "Ad unavailable — tap to retry";
    }

    /// <summary>Idempotent: ResetFor calls this even when the card was never shown.</summary>
    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        EventBus.Cue("ui.close");
    }

    public bool IsOpen => Visible;

    private static string StarGlyphs(int stars)
    {
        int clamped = Math.Clamp(stars, 0, 3);
        return new string('*', clamped) + new string('-', 3 - clamped);
    }
}
