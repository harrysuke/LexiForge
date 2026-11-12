using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Rune draft after forges 1, 3 and 5 (config: heat.draftsAfterForges).
///
/// The draft is the game's differentiator, so it must read as a BUILD, not a
/// settings list (UI-39..UI-45):
///
///  - Three CARDS, each with a glyph, a kind chip, a rarity border treatment and
///    the effect in player language.
///  - **Show the delta, not the description** (UI-40): "Est. +8% coins this run"
///    is evaluable; "+1 coin per letter in words of length >= 5" is not.
///  - The current build sits at the TOP with the new slot as an empty chip
///    (UI-41): the player is completing a set, not picking from a list.
///  - Three options, never four (Hick's law, UI-42). The 4th SLOT is a Mark
///    purchase - that is a slot, not a fourth option.
///  - Skip is free and always present (UI-45): autonomy is not a nice-to-have.
///
/// Rarity/stacking rules live in core/RuneDraft.cs; this screen only renders the
/// offer and reports the choice back to RunService.
/// </summary>
public sealed partial class RuneDraftScreen : Control, IScreen
{
    private readonly Button[] _cards = new Button[3];
    private readonly IconGlyph[] _glyphs = new IconGlyph[3];
    private readonly Label[] _names = new Label[3];
    private readonly Label[] _kinds = new Label[3];
    private readonly Label[] _effects = new Label[3];
    private readonly Label[] _deltas = new Label[3];

    private Label _header = null!;
    private Label _status = null!;
    private Button _reroll = null!;
    private Button _skip = null!;
    private BuildStrip _build = null!;
    private PauseOverlay _pause = null!;
    private IReadOnlyList<RuneData> _offer = Array.Empty<RuneData>();
    private ScrollContainer _cardScroll = null!;
    private int _runIndex;
    private bool _picking;

    public override void _Ready()
    {
        Tok.Setup(GetViewportRect().Size.X);

        var background = new ColorRect { Color = Tok.BgBase, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(background);
        Ui.FullRect(background);

        var column = Ui.AttachColumn(this, 10f, Tok.MarginDp);

        column.AddChild(Ui.Display(Ui.MakeLabel("DRAFT A RUNE", 30f, Tok.Text, HorizontalAlignment.Left), this));
        _header = Ui.MakeLabel("It lasts the whole run", 22f, Tok.TextDim, HorizontalAlignment.Left, wrap: true);
        column.AddChild(_header);

        column.AddChild(Ui.FixedSpacer(4f));
        column.AddChild(Ui.MakeLabel("YOUR BUILD", 20f, Tok.Muted, HorizontalAlignment.Left));
        _build = new BuildStrip();
        column.AddChild(_build);
        column.AddChild(Ui.FixedSpacer(4f));

        // The three cards are >=180 dp each by spec (UI-39), and 3 x 180 + header +
        // build strip + reroll + skip is ~790 dp - taller than the 640 dp canvas.
        // Rather than shrink the cards below spec or drop the delta line, the CARDS
        // scroll and the two decisions stay pinned: autonomy (UI-45) is never
        // something the player has to scroll to find.
        //
        // The scroll sits in a plain host so the EdgeFade can be layered over its
        // bottom edge without being scrolled with the content.
        var scrollHost = new Control
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, Tok.Dp(120f))
        };
        column.AddChild(scrollHost);

        _cardScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto
        };
        scrollHost.AddChild(_cardScroll);
        Ui.FullRect(_cardScroll);
        Ui.StyleScroll(_cardScroll);

        // A hard clip slices through a card's delta line and reads as a bug. The
        // fade says "there is more below" without a word of chrome.
        scrollHost.AddChild(EdgeFade.Create(EdgeFade.Edge.Bottom, 56f, Tok.BgBase));

        // 8 dp gutter on the right: the ScrollContainer stretches its child to the
        // viewport width MINUS the scrollbar, so without this the grabber sits flush
        // against the card's rim and reads as a stray border segment.
        var pad = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        pad.AddThemeConstantOverride("margin_right", Tok.I(8f));
        _cardScroll.AddChild(pad);

        var cards = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", Tok.I(10f));
        pad.AddChild(cards);

        for (int i = 0; i < _cards.Length; i++)
        {
            int index = i; // captured once; the cards are pooled for the session
            Button card = BuildCard(index);
            cards.AddChild(card);
            _cards[i] = card;
        }

        column.AddChild(Ui.FixedSpacer(4f));

        // UI-43: reroll is a small text button with its price inline. Never a
        // modal, never urgent.
        _reroll = Ui.MakeButton("", OnReroll, BtnKind.Ghost, 44f, 20f, null);
        column.AddChild(_reroll);

        // UI-45: skip must exist and must be free.
        _skip = Ui.MakeButton("Skip — take nothing", Skip, BtnKind.Ghost, 44f, 20f, "ui.back");
        column.AddChild(_skip);

        _status = Ui.MakeLabel("", 20f, Tok.Ember, HorizontalAlignment.Center);
        _status.Visible = false;
        column.AddChild(_status);
        // ^ Hidden by default: an empty status line still costs a 20 sp row, and on
        //   the Compact breakpoint that row is the difference between "Skip" being
        //   reachable and not. SetStatus() keeps text and visibility in lockstep.

        _pause = PauseOverlay.AttachTo(this, "Leave run — this forge starts over", LeaveRun,
                                       () => SceneRouter.Instance.GoHome());
    }

    private Button BuildCard(int index)
    {
        // tapCue null: Pick() emits rune.pick with the rune's family index, which
        // selects the timbre (steel / bronze / slate) in the catalogue.
        Button card = Ui.MakeButton("", () => Pick(index), BtnKind.Secondary, 180f, 22f, null);

        var frame = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        frame.AddThemeConstantOverride("margin_left", Tok.I(14f));
        frame.AddThemeConstantOverride("margin_right", Tok.I(14f));
        frame.AddThemeConstantOverride("margin_top", Tok.I(10f));
        frame.AddThemeConstantOverride("margin_bottom", Tok.I(10f));
        frame.SetAnchorsPreset(LayoutPreset.FullRect);
        card.AddChild(frame);

        var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        column.AddThemeConstantOverride("separation", Tok.I(4f));
        frame.AddChild(column);

        var head = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        head.AddThemeConstantOverride("separation", Tok.I(10f));
        column.AddChild(head);

        _glyphs[index] = IconGlyph.Create(Glyph.Star, 26f, Tok.GoldPale, 1.8f);
        _glyphs[index].SizeFlagsVertical = SizeFlags.ShrinkCenter;
        head.AddChild(_glyphs[index]);

        _names[index] = Ui.MakeLabel("", 24f, Tok.Text, HorizontalAlignment.Left);
        _names[index].SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _names[index].MouseFilter = MouseFilterEnum.Ignore;
        head.AddChild(_names[index]);

        _kinds[index] = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Center);
        _kinds[index].MouseFilter = MouseFilterEnum.Ignore;
        _kinds[index].AddThemeStyleboxOverride("normal", Ui.Box(Tok.Surf1, 6f));
        head.AddChild(_kinds[index]);

        _effects[index] = Ui.MakeLabel("", 20f, Tok.TextDim, HorizontalAlignment.Left, wrap: true);
        _effects[index].MouseFilter = MouseFilterEnum.Ignore;
        _effects[index].SizeFlagsVertical = SizeFlags.ExpandFill;
        column.AddChild(_effects[index]);

        var divider = new ColorRect { Color = Tok.LineHair, CustomMinimumSize = new Vector2(0, Tok.Dp(1f)) };
        divider.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(divider);

        // UI-40: the single highest-value line in the draft.
        _deltas[index] = Ui.MakeLabel("", 20f, Tok.Gold, HorizontalAlignment.Left);
        _deltas[index].MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(_deltas[index]);

        return card;
    }

    /// <summary>
    /// The transient line under the two decisions. Empty means no row at all;
    /// any real message un-hides the label, so a failed reroll is never silent.
    /// </summary>
    private void SetStatus(string text)
    {
        _status.Text = text;
        _status.Visible = text.Length > 0;
    }

    public void OnEnter(object? payload)
    {
        _runIndex = payload is DraftPayload draft ? draft.RunIndex : 0;
        _picking = false;
        SetStatus("");
        _pause.Close();
        Render(RunService.Instance.DraftOffer());
        EventBus.Cue("rune.draft.open");
    }

    public void OnExit() => _pause.Close();
    public void OnBack() => _pause.Toggle();

    private void Render(IReadOnlyList<RuneData> offer)
    {
        _offer = offer;
        _picking = false;
        _header.Text = "It lasts the whole run";
        SetStatus("");

        _build.Refresh(RunService.Instance.OwnedRunes, 3, true);

        for (int i = 0; i < _cards.Length; i++)
        {
            Button card = _cards[i];
            bool hasRune = i < offer.Count && offer[i] is not null;
            card.Visible = hasRune;
            if (!hasRune) continue;

            RuneData rune = offer[i];
            card.Modulate = Colors.White;
            card.Scale = Vector2.One;
            card.PivotOffset = card.Size * 0.5f;

            _glyphs[i].SetGlyph(RuneChip.FamilyGlyph(rune.Kind));
            _names[i].Text = rune.Name.ToUpperInvariant();
            _kinds[i].Text = rune.Kind.ToString().ToUpperInvariant();
            _effects[i].Text = rune.Effect;
            _deltas[i].Text = DeltaLine(rune);
            card.Disabled = false;
            ApplyRarity(card, rune.Rarity);

            AnalyticsService.Instance.Record("draft_card_shown", rune.Id,
                Mathf.RoundToInt(rune.CoinDeltaPct * 100f).ToString());
        }

        int cost = ConfigService.Instance.Config.Sinks.RuneDraftRerollCoins;
        _reroll.Text = RunService.Instance.CanRerollFree
            ? "Reroll · free (Mirror)"
            : $"Reroll · 1 Mark or {cost} coins";
    }

    /// <summary>
    /// UI-40: show the delta, not the description. For utility runes (which have
    /// no coin delta by design - see RuneEffects) state the concrete change in
    /// player language instead of a percentage that would be 0%.
    /// </summary>
    private static string DeltaLine(RuneData rune)
    {
        if (Mathf.Abs(rune.CoinDeltaPct) > 0.0001f)
        {
            int pct = Mathf.RoundToInt((float)rune.CoinDeltaPct * 100f);
            return $"Est. {(pct >= 0 ? "+" : "")}{pct}% coins this run";
        }
        return rune.Id switch
        {
            "anvil" => "Est. +1 free reveal each forge",
            "gale" => "Est. +1 wild tile on the rack",
            "lens" => "Est. slot lengths visible all run",
            "mirror" => "Est. 1 free reroll at the next draft",
            "quill" => "Est. −2 Heat on a perfect forge",
            "anchor" => "Est. −3 Heat every forge",
            "guild" => "Est. +1 Mark at run end",
            _ => "Est. +1 upgrade this run"
        };
    }

    /// <summary>Rarity by BORDER WEIGHT, not colour: it survives greyscale (6.7).</summary>
    private static void ApplyRarity(Button card, RuneTier rarity)
    {
        float radius = Tok.RadiusCardDp;
        StyleBoxFlat normal = rarity switch
        {
            RuneTier.Rare => Ui.StrokeBox(Tok.Surf2, Tok.Gold, radius, 2f),
            RuneTier.Uncommon => Ui.StrokeBox(Tok.Surf1, Tok.A(Tok.Gold, 0.4f), radius, 1f),
            _ => Ui.StrokeBox(Tok.Surf1, Tok.LineHair, radius, 1f)
        };
        card.AddThemeStyleboxOverride("normal", normal);
        card.AddThemeStyleboxOverride("hover", Ui.StrokeBox(Tok.Surf2, Tok.A(Tok.Gold, 0.7f), radius, 2f));
        card.AddThemeStyleboxOverride("pressed", Ui.StrokeBox(Tok.Surf3, Tok.Gold, radius, 2f));
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
            SetStatus($"Need 1 Mark or {cfg.Sinks.RuneDraftRerollCoins} coins.");
            return;
        }
        EventBus.Cue("rune.reroll");
        Render(RunService.Instance.RerollOffer());
    }

    private void Pick(int index)
    {
        if (_picking || index < 0 || index >= _offer.Count) return;
        _picking = true;

        RuneData rune = _offer[index];
        // Magnitude = rune family, not a random index: the catalogue maps 0/1/2
        // onto steel (Score), bronze (Economy) and slate (Utility).
        EventBus.Cue("rune.pick", FamilyIndex(rune.Kind));
        RunService.Instance.Draft(rune);
        AnalyticsService.Instance.Record("rune_pick", rune.Id);

        // UI-44: the chosen card scales, the others fade, and the chip FLIES to
        // the build strip. That flight is the investment becoming real.
        for (int i = 0; i < _cards.Length; i++)
        {
            if (!_cards[i].Visible) continue;
            if (i == index)
            {
                _cards[i].PivotOffset = _cards[i].Size * 0.5f;
                UiAnim.On(_cards[i], "pick")
                      .TweenProperty(_cards[i], "scale", new Vector2(1.04f, 1.04f), UiAnim.Dur(0.18f))
                      .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
            }
            else
            {
                UiAnim.FadeTo(_cards[i], 0.4f, 0.18f);
            }
        }

        FlyChipThenContinue(rune, _cards[index]);
    }

    private async void FlyChipThenContinue(RuneData rune, Control source)
    {
        await ToSignal(GetTree().CreateTimer(0.18, false), SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree()) return;

        _build.Refresh(RunService.Instance.OwnedRunes, 3, false);
        await ToSignal(GetTree().CreateTimer(0.05, false), SceneTreeTimer.SignalName.Timeout);

        RuneChip chip = RuneChip.Create(rune, false);
        AddChild(chip);
        chip.Position = source.GlobalPosition - GlobalPosition + new Vector2(Tok.Dp(8f), Tok.Dp(8f));
        UiAnim.Arc(chip, chip.Position, BuildStripCenter(), 24f, Tok.TChip, ContinueToNextForge);
    }

    private Vector2 BuildStripCenter()
    {
        Vector2 origin = _build.GlobalPosition - GlobalPosition;
        if (_build.GetChildCount() > 0 && _build.GetChild(_build.GetChildCount() - 1) is Control last)
            return last.GlobalPosition - GlobalPosition + last.Size * 0.5f;
        return origin + _build.Size * 0.5f;
    }

    private void ContinueToNextForge()
    {
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

    private void Skip()
    {
        if (_picking) return;
        _picking = true;
        // UI-45: skipping is free and costs nothing but the rune.
        AnalyticsService.Instance.Record("draft_skip", _runIndex.ToString());
        ContinueToNextForge();
    }

    private void LeaveRun()
    {
        AdManager.Instance.SetAbandoned(true);
        AdManager.Instance.SetInRun(false);
        SceneRouter.Instance.GoHome();
    }
}
