using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Give Up reveal card (GDD 6.2.1 / UI_DESIGN UI-64).
///
/// A stuck player is never trapped. One quiet tap shows EVERY solution word in
/// reveal order, marks what they already forged, and calls out the keystone —
/// the final "aha" word — separately. There is deliberately no confirmation
/// step: the button on the HUD is already a deliberate, low-traffic decision,
/// and stacking a confirmation on top of it would punish the exact player the
/// feature exists for.
///
/// This is a CARD, not a wall (UI-P3): tapping the scrim, pressing back, or
/// "KEEP FORGING" all dismiss it and return to the identical forge state.
/// Nothing is forfeited — no coins, stars, Heat, hints or progression change.
/// The only cost is information: the puzzle is now spoiled, which is the
/// player's own choice to make.
///
/// Layout follows ResultsCard, not CenterContainer: a container would re-sort
/// and cancel the 40 dp entrance rise, and an autowrapped Label inside one
/// reports a minimum height measured at width 0 (AudioSettingsPanel section).
/// The card is sized by summing visible row minima and positioned by hand.
/// </summary>
public sealed partial class GiveUpModal : Control
{
    [Signal] public delegate void KeepForgingRequestedEventHandler();
    [Signal] public delegate void RestartRequestedEventHandler();
    [Signal] public delegate void LeaveRunRequestedEventHandler();

    private const int GridColumns = 2;
    private const float SeparationDp = 10f;
    private const float ChipHeightDp = 44f;
    private const float GridHSepDp = 8f;
    private const float GridVSepDp = 8f;

    private ColorRect _dim = null!;
    private PanelContainer _card = null!;
    private VBoxContainer _column = null!;
    private Label _subtitle = null!;
    private Control _scrollFrame = null!;
    private ScrollContainer _wordScroll = null!;
    private EdgeFade _fade = null!;
    private GridContainer _words = null!;
    private Label _keystone = null!;

    private readonly List<Chip> _chips = new();
    private int _visibleWords;
    private int _foundCount;
    private string _baseSubtitle = "";

    public bool IsOpen => Visible;

    /// <summary>A pooled word row. Plain C# holder — the Godot nodes live in _words.</summary>
    private sealed class Chip
    {
        public PanelContainer Root = null!;
        public Label Word = null!;
        public IconGlyph Mark = null!;
    }

    public GiveUpModal()
    {
        Visible = false;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Ui.FullRect(this);

        // 0.82 (not 0.92): the grid stays faintly legible behind the card, so
        // the revealed words can still be mapped back to the slots they fill.
        _dim = new ColorRect
        {
            Color = Tok.A(Tok.BgVoid, 0.82f),
            MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_dim);
        Ui.FullRect(_dim);

        _card = Ui.MakePanel(Tok.Surf1, Tok.RadiusCardDp);
        _card.MouseFilter = MouseFilterEnum.Stop;
        AddChild(_card);

        _column = new VBoxContainer();
        _column.AddThemeConstantOverride("separation", Tok.I(SeparationDp));
        _card.AddChild(_column);

        _column.AddChild(Ui.Display(Ui.MakeLabel("The words were", 30f, Tok.Text), this));

        _subtitle = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left);
        _column.AddChild(_subtitle);

        // The shipped difficulty curve reaches TWELVE solution words (3 + 9*t),
        // and 289 of 400 forges exceed eight. A plain 2-column grid would push
        // the exits off the card, so only the WORD LIST scrolls: title, keystone
        // callout, "no cost" line and all three exits stay pinned and visible.
        // A plain Control frame lets the bottom EdgeFade overlay the scroll clip
        // without the ScrollContainer treating it as scrollable content.
        _scrollFrame = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _column.AddChild(_scrollFrame);

        _wordScroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            MouseFilter = MouseFilterEnum.Stop
        };
        Ui.StyleScroll(_wordScroll);
        _scrollFrame.AddChild(_wordScroll);
        Ui.FullRect(_wordScroll);

        _words = new GridContainer { Columns = GridColumns };
        _words.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _words.AddThemeConstantOverride("h_separation", Tok.I(GridHSepDp));
        _words.AddThemeConstantOverride("v_separation", Tok.I(GridVSepDp));
        _wordScroll.AddChild(_words);

        _fade = EdgeFade.Create(EdgeFade.Edge.Bottom, 28f, Tok.Surf1);
        _fade.Visible = false;
        _scrollFrame.AddChild(_fade);

        _column.AddChild(BuildKeystoneRow());

        // Deliberately short lines, NOT autowrapped: see the class comment.
        _column.AddChild(Ui.MakeLabel("No cost. No penalty.", 20f, Tok.Muted, HorizontalAlignment.Left));

        // Every row uses tapCue null: Open()/Close() emit ui.open / ui.close, so a
        // tap cue here would double up (AUDIO_DESIGN section 4.2 rule 3).
        _column.AddChild(Ui.MakeButton("KEEP FORGING",
            () => EmitSignal(SignalName.KeepForgingRequested), BtnKind.Primary, 56f, 22f, null));
        _column.AddChild(Ui.MakeButton("START THIS FORGE OVER",
            () => EmitSignal(SignalName.RestartRequested), BtnKind.Secondary, 56f, 22f, null));
        _column.AddChild(Ui.MakeButton("Leave run",
            () => EmitSignal(SignalName.LeaveRunRequested), BtnKind.Ghost, 44f, 20f, null));

        Resized += OnResized;
    }

    private Control BuildKeystoneRow()
    {
        var row = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Tok.Surf2 };
        style.SetCornerRadiusAll(Tok.I(12f));
        style.ContentMarginLeft = Tok.Dp(10f);
        style.ContentMarginRight = Tok.Dp(10f);
        style.ContentMarginTop = Tok.Dp(4f);
        style.ContentMarginBottom = Tok.Dp(4f);
        row.AddThemeStyleboxOverride("panel", style);
        row.CustomMinimumSize = new Vector2(0, Tok.Dp(44f));

        var line = Ui.MakeRow(6f);
        row.AddChild(line);

        line.AddChild(IconGlyph.Create(Glyph.Star, 18f, Tok.Gold, 2f));

        var tag = Ui.MakeLabel("KEYSTONE", 20f, Tok.Muted, HorizontalAlignment.Left);
        tag.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        line.AddChild(tag);

        _keystone = Ui.MakeLabel("", 24f, Tok.Gold, HorizontalAlignment.Right);
        _keystone.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(_keystone);
        return row;
    }

    private Chip AddChip()
    {
        var root = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Tok.Surf2 };
        style.SetCornerRadiusAll(Tok.I(12f));
        style.ContentMarginLeft = Tok.Dp(10f);
        style.ContentMarginRight = Tok.Dp(10f);
        style.ContentMarginTop = Tok.Dp(4f);
        style.ContentMarginBottom = Tok.Dp(4f);
        root.AddThemeStyleboxOverride("panel", style);
        root.CustomMinimumSize = new Vector2(0, Tok.Dp(44f));

        var line = Ui.MakeRow(6f);
        root.AddChild(line);

        var word = Ui.MakeLabel("", 22f, Tok.Text, HorizontalAlignment.Left);
        word.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.AddChild(word);

        var mark = IconGlyph.Create(Glyph.Dot, 16f, Tok.Muted, 2f);
        mark.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        mark.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        line.AddChild(mark);

        _words.AddChild(root);
        var chip = new Chip { Root = root, Word = word, Mark = mark };
        _chips.Add(chip);
        return chip;
    }

    private static void StyleChip(Chip chip, string word, bool found, bool keystone)
    {
        chip.Word.Text = word.ToUpperInvariant();
        chip.Word.AddThemeColorOverride("font_color",
            keystone ? Tok.Gold : found ? Tok.Jade : Tok.Text);
        chip.Mark.SetGlyph(found ? Glyph.Check : keystone ? Glyph.Star : Glyph.Dot);
        chip.Mark.SetColor(found ? Tok.Jade : keystone ? Tok.Gold : Tok.Muted);
    }

    /// <summary>
    /// Shows the whole answer. <paramref name="session"/> is read-only here: the
    /// modal never mutates ForgeSession, so closing it restores play byte-for-byte.
    /// </summary>
    public void Open(ForgeData forge, ForgeSession session)
    {
        if (forge is null || session is null || Visible) return;

        var found = new HashSet<string>(session.FoundSolutions, StringComparer.OrdinalIgnoreCase);
        var words = forge.SolutionWords;

        // Pooled: a forge with 4 words reuses the 8 chips created by an earlier
        // 8-word forge. No QueueFree between forges (GDD 11.5).
        while (_chips.Count < words.Count) AddChip();
        for (int i = 0; i < _chips.Count; i++)
        {
            bool shown = i < words.Count;
            _chips[i].Root.Visible = shown;
            if (!shown) continue;
            string word = words[i];
            bool keystone = string.Equals(word, forge.KeystoneWord, StringComparison.OrdinalIgnoreCase);
            StyleChip(_chips[i], word, found.Contains(word), keystone);
        }

        _visibleWords = words.Count;
        _foundCount = found.Count;
        _baseSubtitle = $"{found.Count} of {words.Count} forged  ·  {forge.Theme}";
        _subtitle.Text = _baseSubtitle;
        _keystone.Text = forge.KeystoneWord.ToUpperInvariant();

        Visible = true;
        MoveToFront();
        // A re-opened card must always start at the top of the reveal order.
        _wordScroll.ScrollVertical = 0;
        LayoutCard();
        // GridContainer re-sorts on the next layout pass, so the card is measured
        // again before it is allowed to rise (ResultsCard section: measuring once
        // here settles the card one row short).
        CallDeferred(nameof(OpenDeferred));
        EventBus.Cue("ui.open");
    }

    private void OpenDeferred()
    {
        if (!Visible) return;
        LayoutCard();
        UiAnim.RiseIn(_card, 40f, Tok.TCard);
    }

    private void OnResized()
    {
        if (_card is null || !Visible) return;
        LayoutCard();
    }

    /// <summary>
    /// Height is summed from the PINNED rows, and whatever space remains is given
    /// to the word list. Never ask the panel for its combined minimum: Open()
    /// toggles chip visibility and measures immediately, and a container's
    /// minimum is only valid after the next layout pass.
    ///
    /// Measured at the 360x640 dp canvas: 8 words -> 579 dp, no scrolling.
    /// 12 words -> the grid would need ~304 dp, so the list scrolls inside a
    /// 237 dp window and the card stays at 616 dp with every exit still pinned.
    /// </summary>
    private void LayoutCard()
    {
        if (_card is null || _column is null) return;

        float maxHeight = Size.Y - Tok.Dp(24f);
        float pinned = 0f;
        int pinnedRows = 0;
        foreach (Node child in _column.GetChildren())
        {
            if (child is not Control control || !control.Visible || control == _scrollFrame) continue;
            pinned += control.GetCombinedMinimumSize().Y;
            pinnedRows++;
        }

        // _scrollFrame is always visible, so pinnedRows is also the gap count.
        float gaps = Tok.Dp(SeparationDp) * pinnedRows;
        float margins = Tok.Dp(12f); // Ui.MakePanel: 6 dp top + 6 dp bottom

        int rows = Mathf.CeilToInt(_visibleWords / (float)GridColumns);
        float natural = Mathf.Max(0, rows) * Tok.Dp(ChipHeightDp)
                      + Mathf.Max(0, rows - 1) * Tok.I(GridVSepDp);
        float available = maxHeight - pinned - gaps - margins;
        float minScroll = Tok.Dp(ChipHeightDp);
        float scrollHeight = Mathf.Clamp(natural, minScroll, Mathf.Max(minScroll, available));
        bool scrolls = natural > scrollHeight + 1f;

        _scrollFrame.CustomMinimumSize = new Vector2(0, scrollHeight);
        _fade.Visible = scrolls;
        // Short, non-autowrapped line: a long suffix would silently clip on a
        // 312 dp card. The Gold grabber and the bottom fade are the other cues.
        _subtitle.Text = scrolls
            ? $"{_foundCount}/{_visibleWords} forged  ·  scroll"
            : _baseSubtitle;

        float height = Mathf.Min(pinned + scrollHeight + gaps + margins, maxHeight);
        float width = Mathf.Min(Size.X - Tok.Dp(Tok.MarginDp) * 2f, Tok.Dp(Tok.DesignWidthDp - 40f));
        width = Mathf.Max(width, _card.GetCombinedMinimumSize().X);

        _card.Size = new Vector2(width, height);
        _card.Position = new Vector2((Size.X - width) * 0.5f, (Size.Y - height) * 0.5f);
    }

    /// <summary>Idempotent: ForgeScreen calls this on enter, exit and every ResetFor.</summary>
    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        _card.Modulate = new Color(1, 1, 1, 1);
        EventBus.Cue("ui.close");
    }

    /// <summary>Tapping the scrim is the same decision as KEEP FORGING.</summary>
    public override void _GuiInput(InputEvent @event)
    {
        bool tapped = @event is InputEventScreenTouch { Pressed: true } ||
                      @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
        if (!tapped) return;
        EmitSignal(SignalName.KeepForgingRequested);
        AcceptEvent();
    }
}
