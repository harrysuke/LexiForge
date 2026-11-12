using System;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Forge-complete card. A CARD the player dismisses, never a wall (UI-P3), and it
/// rises OVER the grid, which stays visible at 30% dim - the evidence of the
/// player's competence must stay on screen while they are paid for it (UI-46).
///
/// Payment order (UI-47), total 2.4 s, skippable by tap:
///   stars (one at a time) → coins (count up) → jar burst → Heat chip.
/// Coins come last but one because the jar burst is the variable reward and it
/// must be the final surprise.
/// </summary>
public sealed partial class ResultsCard : Control
{
    [Signal] public delegate void ContinueRequestedEventHandler();
    [Signal] public delegate void DoubleHaulRequestedEventHandler();
    [Signal] public delegate void JarBurstRequestedEventHandler();

    private ColorRect _dim = null!;
    private PanelContainer _card = null!;
    private VBoxContainer _column = null!;
    private Label _title = null!;
    private readonly StarIcon[] _stars = new StarIcon[3];
    private Label _coins = null!;
    private Label _bonusLine = null!;
    private Label _heatChip = null!;
    private RewardedRow _double = null!;

    private int _generation;
    private bool _doubled;
    private double _openedAt;
    private int _totalCoins;
    private int _lastStars;
    private double _heatBefore;
    private double _heatAfter;

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        // 30%, not 82%: the grid must remain readable behind the card.
        _dim = new ColorRect { Color = Tok.A(Tok.BgVoid, 0.30f) };
        AddChild(_dim);
        Ui.FullRect(_dim);

        _card = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Tok.Surf1 };
        style.SetCornerRadiusAll(Tok.I(Tok.RadiusCardDp));
        style.ContentMarginLeft = Tok.I(20f);
        style.ContentMarginRight = Tok.I(20f);
        style.ContentMarginTop = Tok.I(20f);
        style.ContentMarginBottom = Tok.I(16f);
        _card.AddThemeStyleboxOverride("panel", style);
        AddChild(_card);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(12f));
        _card.AddChild(column);
        _column = column;

        _title = Ui.Display(Ui.MakeLabel("FORGED", 30f, Tok.Text), this);
        column.AddChild(_title);

        var starsRow = Ui.MakeRow(8f);
        starsRow.Alignment = BoxContainer.AlignmentMode.Center;
        column.AddChild(starsRow);
        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i] = StarIcon.Create(44f);
            starsRow.AddChild(_stars[i]);
        }

        _coins = Ui.MakeLabel("+0 coins", 30f, Tok.Gold);
        column.AddChild(_coins);

        _bonusLine = Ui.MakeLabel("", 20f, Tok.Muted);
        column.AddChild(_bonusLine);

        _heatChip = Ui.MakeLabel("", 22f, Tok.Muted);
        _heatChip.Modulate = new Color(1, 1, 1, 0);
        column.AddChild(_heatChip);

        // UI-52: the largest element on the card, because the card's entire purpose
        // is to get the player back into a grid before the feeling fades.
        column.AddChild(Ui.MakeButton("ONE MORE FORGE", () => EmitSignal(SignalName.ContinueRequested),
                                      BtnKind.Primary, 72f, 24f, "ui.tap"));

        // UI-51: opt-in row BELOW the primary, never auto-focused, never a popup.
        _double = RewardedRow.Create("Double your coins", "", () => EmitSignal(SignalName.DoubleHaulRequested), 64f);
        column.AddChild(_double);

        Resized += OnResized;
    }

    private void OpenDeferred()
    {
        LayoutCard();
        UiAnim.RiseIn(_card, 40f, Tok.TCard);
    }

    private void OnResized()
    {
        // No `_dim.Size = Size` here: the dim is anchored FullRect, so assigning a
        // size is redundant AND wrong - Godot overrides it after _ready() and warns
        // about "non-equal opposite anchors". The anchors already follow us.
        if (_dim is null) return;
        LayoutCard();
    }

    /// <summary>
    /// The card is positioned by hand rather than by a CenterContainer: a
    /// container would re-sort and cancel the 40 dp entrance rise.
    ///
    /// The height is summed from the visible rows instead of asked of the panel.
    /// `Open()` toggles the rewarded row's visibility and immediately measures, and
    /// a container's combined minimum size is only valid AFTER the next layout pass
    /// - so the measured card came out one row short and clipped the ad offer.
    /// </summary>
    private void LayoutCard()
    {
        if (_card is null || _column is null) return;

        float content = 0f;
        int shown = 0;
        foreach (Node child in _column.GetChildren())
        {
            if (child is not Control control || !control.Visible) continue;
            content += control.GetCombinedMinimumSize().Y;
            shown++;
        }
        if (shown > 1) content += Tok.Dp(12f) * (shown - 1);

        float height = content + Tok.Dp(20f) + Tok.Dp(16f);
        // The card takes the full safe column, not its minimum: at the old 300 dp
        // floor the rewarded row had 480 px for "Double your coins" and ellipsised
        // it. The minimum is only a floor, never a ceiling.
        float column = Mathf.Min(Size.X - Tok.Dp(Tok.MarginDp) * 2f, Tok.Dp(420f));
        float width = Mathf.Max(_card.GetCombinedMinimumSize().X, column);

        _card.Size = new Vector2(width, height);
        _card.Position = new Vector2((Size.X - width) * 0.5f, (Size.Y - height) * 0.5f);
    }

    public void Open(ForgeOutcome outcome, double heatBefore, double heatAfter, bool canDouble)
    {
        _generation++;
        _doubled = false;
        _totalCoins = outcome.TotalCoins;
        _lastStars = outcome.Result.Stars;
        _heatBefore = heatBefore;
        _heatAfter = heatAfter;
        _openedAt = Time.GetTicksMsec() / 1000.0;

        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i].SetFilled(false);
            _stars[i].Scale = Vector2.One;
        }
        _coins.Text = "+0 coins";
        _bonusLine.Text = outcome.BonusWords.Count == 0
            ? $"{outcome.SolveSeconds:0}s  ·  par {outcome.ParSeconds:0}s"
            : $"{outcome.BonusWords.Count} bonus words  ·  {outcome.SolveSeconds:0}s / par {outcome.ParSeconds:0}s";
        _heatChip.Modulate = new Color(1, 1, 1, 0);
        _double.Visible = canDouble && outcome.TotalCoins > 0;
        _double.Configure("Double your coins", $"{outcome.TotalCoins} → {outcome.TotalCoins * 2}",
                          () => EmitSignal(SignalName.DoubleHaulRequested), Glyph.None);
        _double.SetLoading(false);

        Visible = true;
        LayoutCard();
        // Row heights that depend on a container's re-sort are only valid after the
        // next frame, so the card is measured again before it is allowed to rise.
        // Measuring once here would settle the card one row short.
        CallDeferred(nameof(OpenDeferred));
        EventBus.Cue("ui.open");
        RunSequence(_generation, outcome.Result.Stars);
    }

    /// <summary>The payment sequence. Tap-to-skip collapses it to the end state.</summary>
    private async void RunSequence(int generation, int stars)
    {
        for (int i = 0; i < 3; i++)
        {
            await ToSignal(GetTree().CreateTimer(0.18, false), SceneTreeTimer.SignalName.Timeout);
            if (generation != _generation) return;
            if (i < stars)
            {
                _stars[i].SetFilled(true);
                _stars[i].Land();
                // Three stars must feel like three events: distinct pitch per star.
                EventBus.Cue("tile.place", i + 1);
                UiAnim.Haptic(10);
            }
        }

        await ToSignal(GetTree().CreateTimer(0.18, false), SceneTreeTimer.SignalName.Timeout);
        if (generation != _generation) return;
        UiAnim.CountUp(_coins, 0, _totalCoins, 0.7f, "+{0} coins");
        EventBus.Cue("coin.earn");

        await ToSignal(GetTree().CreateTimer(0.70, false), SceneTreeTimer.SignalName.Timeout);
        if (generation != _generation) return;
        EmitSignal(SignalName.JarBurstRequested);

        await ToSignal(GetTree().CreateTimer(0.40, false), SceneTreeTimer.SignalName.Timeout);
        if (generation != _generation) return;
        ShowHeatChip();
        if (stars >= 3) EventBus.Cue("forge.perfect");
    }

    private void ShowHeatChip()
    {
        double delta = _heatAfter - _heatBefore;
        _heatChip.Text = $"Heat {(delta >= 0 ? "+" : "")}{delta:0}  →  {_heatAfter:0}/100";
        // Jade when Heat FELL (the cool is a gain), Rust text when it rose.
        _heatChip.AddThemeColorOverride("font_color", delta <= 0 ? Tok.Jade : Tok.RustText);
        _heatChip.Modulate = new Color(1, 1, 1, 1);
    }

    private void SkipToEnd()
    {
        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i].SetFilled(i < _lastStars);
            _stars[i].Scale = Vector2.One;
        }
        _coins.Text = $"+{_totalCoins} coins";
        ShowHeatChip();
    }

    public override void _GuiInput(InputEvent @event)
    {
        bool tapped = @event is InputEventScreenTouch { Pressed: true } ||
                      @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
        if (!tapped) return;

        // Tap-to-skip (UI-47). Recorded so the pacing claim is falsifiable.
        double elapsed = Time.GetTicksMsec() / 1000.0 - _openedAt;
        if (elapsed < 2.4)
        {
            AnalyticsService.Instance.Record("results_skipped", ((int)(elapsed * 1000)).ToString());
            _generation++;
            SkipToEnd();
        }
        AcceptEvent();
    }

    /// <summary>Called by the screen only after the rewarded callback granted the bonus.</summary>
    public void MarkDoubled(int totalCoins)
    {
        if (_doubled) return;
        _doubled = true;
        _totalCoins = totalCoins;
        _coins.Text = $"+{totalCoins} coins";
        UiAnim.Pulse(_coins, 1.12f, 0.26f);
        _double.FlashGranted();
        _double.Configure("Coins doubled", $"{totalCoins}", () => { }, Glyph.Coin);
    }

    /// <summary>Visible feedback on an ad no-fill; the overlay hides the HUD toast.</summary>
    public void NoteAdUnavailable()
    {
        // GDD 8.3 / section 9.4: no error, no toast, no retry prompt - the row
        // quietly reverts and the coin path remains.
        _double.SetLoading(false);
        _double.Configure("Double your coins", $"{_totalCoins} → {_totalCoins * 2}",
                          () => EmitSignal(SignalName.DoubleHaulRequested), Glyph.Coin);
    }

    public void SetAdLoading(bool loading) => _double.SetLoading(loading);

    /// <summary>Idempotent: ResetFor calls this even when the card was never shown.</summary>
    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        _generation++;
        EventBus.Cue("ui.close");
    }
}
