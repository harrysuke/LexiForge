using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The Act band: compose strip, letter rack and action row.
///
/// Three design decisions are load-bearing here:
///
///  - **Tap-to-spell is first class** (UI-34). A third of casual players never
///    discover drag, so taps append to the same word and a double-tap on the
///    last tile submits. Drag still works and still submits on release.
///  - **The action row is [Clear] [FORGE flex] [Shuffle]** (UI_VISUAL_SYSTEM
///    D-03). Hint is a PAID action and must not sit next to Shuffle, the
///    most-hammered free control, so it is pinned to the compose strip instead.
///  - **The selection line is the feedback, not the tiles** (UI-33). A thumb
///    covers the tile it selects, so a 6 dp line with a 12 dp glow is what the
///    player actually reads mid-gesture.
///
/// Tiles are pooled and re-placed by ResetFor - no QueueFree between forges
/// (GDD 11.5).
/// </summary>
public sealed partial class LetterRack : Control
{
    [Signal] public delegate void WordSubmittedEventHandler(string word);
    [Signal] public delegate void ShuffleRequestedEventHandler();
    [Signal] public delegate void HintRequestedEventHandler();

    /// <summary>Fires on the first tile of a gesture. The screen uses it to measure
    /// friction at session start (analytics event ui_first_interactive).</summary>
    [Signal] public delegate void TilePickedEventHandler(int index);

    public const int MinWordLength = ForgeSession.MinWordLength;

    private readonly List<RackTile> _tiles = new();
    private readonly List<int> _selection = new();

    private char[] _letters = Array.Empty<char>();
    private bool[] _used = Array.Empty<bool>();

    private Control _strip = null!;
    private Label _wordLabel = null!;
    private Button _hintButton = null!;
    private Label _hintLabel = null!;
    private HBoxContainer _actions = null!;
    private Button _clearButton = null!;
    private Button _forgeButton = null!;
    private Button _shuffleButton = null!;
    private Button _revealButton = null!;

    private Layout _layout = Layout.For(1920f, 1080f);
    private float _tilePx = 200f;
    private bool _dragging;
    private int _gestureAdds;
    private int _lastTapTile = -1;
    private double _lastTapAt;
    private int _nudgeTile = -1;
    private float _nudgePhase;
    private float _rejectDot;
    private bool _revealOffer;
    private Action? _onReveal;
    private int _revealGeneration;

    public string CurrentWord => BuildWord();
    public bool IsDragging => _dragging;

    /// <summary>
    /// Global centre of the last selected tile. Captured by the forge view before
    /// it clears the selection, so a bonus coin can originate at the object the
    /// player touched instead of the screen edge (attribution lever).
    /// </summary>
    public Vector2? SelectionCenterGlobal()
    {
        if (_selection.Count == 0) return null;
        return _tiles[_selection[^1]].GetGlobalRect().GetCenter();
    }

    /// <summary>
    /// Height the Act band needs for this layout and bank size, in dp. The screen
    /// uses it to reserve the band before the rack lays itself out, so the grid
    /// never jumps when a forge starts. It mirrors <see cref="LayoutAll"/> exactly,
    /// including the width-driven tile shrink.
    /// </summary>
    public static float RequiredHeightDp(Layout layout, int tileCount, float availableWidthDp)
    {
        int rows = tileCount <= 4 ? 1 : 2;
        int topRow = rows == 1 ? tileCount : tileCount / 2;
        int widest = Mathf.Max(1, Mathf.Max(topRow, tileCount - topRow));
        float tile = Mathf.Min(layout.RackTileDp, (availableWidthDp - 8f * (widest - 1)) / widest);
        tile = Mathf.Max(tile, 48f);
        float rack = rows * tile + (rows - 1) * 8f;
        // bottom inset (UI-04) + action row + gaps + rack + compose strip
        return Tok.BottomInsetDp + layout.ActionDp + 8f + rack + 8f + layout.ComposeDp;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        BuildChrome();
        SetProcess(true);
    }

    private void BuildChrome()
    {
        _strip = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_strip);

        _wordLabel = Ui.MakeLabel("", 34f, Tok.Gold, HorizontalAlignment.Left);
        _wordLabel.MouseFilter = MouseFilterEnum.Ignore;
        _wordLabel.VerticalAlignment = VerticalAlignment.Center;
        _strip.AddChild(_wordLabel);

        // UI-35: the hint shows its remaining COUNT as a badge, never a price.
        // The price appears on tap, inside the confirm sheet.
        _hintButton = Ui.MakeButton("", () => EmitSignal(SignalName.HintRequested),
                                    BtnKind.Icon, 0f, 20f, null);
        _hintButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _strip.AddChild(_hintButton);

        var hintRow = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        hintRow.AddThemeConstantOverride("separation", Tok.I(3f));
        hintRow.SetAnchorsPreset(LayoutPreset.FullRect);
        _hintButton.AddChild(hintRow);
        hintRow.AddChild(IconGlyph.Create(Glyph.Bulb, 18f, Tok.Gold, 1.8f));
        _hintLabel = Ui.MakeLabel("", 20f, Tok.Gold, HorizontalAlignment.Center);
        _hintLabel.MouseFilter = MouseFilterEnum.Ignore;
        hintRow.AddChild(_hintLabel);

        // D-03 action row: Clear (56) · FORGE (flex, Ember) · Shuffle (72).
        _actions = new HBoxContainer();
        _actions.AddThemeConstantOverride("separation", Tok.I(8f));
        AddChild(_actions);

        _clearButton = Ui.MakeButton("", ClearSelection, BtnKind.Ghost, Tok.BtnSecondaryDp, 20f, null);
        _clearButton.CustomMinimumSize = new Vector2(Tok.Dp(56f), 0);
        _clearButton.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        _clearButton.AddChild(Centre(IconGlyph.Create(Glyph.Close, 18f, Tok.Muted, 2f)));
        _actions.AddChild(_clearButton);

        // tapCue null: submission is answered synchronously by a word.* cue in
        // ForgeScreen, so a tap here would double up (AUDIO_DESIGN section 4.2).
        _forgeButton = Ui.MakeButton("FORGE", SubmitCurrent, BtnKind.Primary, 0f, 24f, null);
        _forgeButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _actions.AddChild(_forgeButton);

        _shuffleButton = Ui.MakeButton("", Shuffle, BtnKind.Icon, 0f, 20f, null);
        _shuffleButton.CustomMinimumSize = new Vector2(Tok.Dp(72f), Tok.Dp(72f));
        _shuffleButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _shuffleButton.AddChild(Centre(IconGlyph.Create(Glyph.Shuffle, 26f, Tok.Gold, 2.2f)));
        _actions.AddChild(_shuffleButton);

        // UI-37: the struggle reveal is a soft button under the rack - no modal,
        // no confirm, no cost. Declining requires no action: it fades in 6 s.
        _revealButton = Ui.MakeButton("Reveal one letter", () => TakeRevealOffer(), BtnKind.Ghost, 44f, 20f, null);
        _revealButton.Visible = false;
        AddChild(_revealButton);
    }

    private static Control Centre(Control child)
    {
        var centre = new CenterContainer { MouseFilter = MouseFilterEnum.Ignore };
        centre.SetAnchorsPreset(LayoutPreset.FullRect);
        centre.AddChild(child);
        return centre;
    }

    public void ApplyLayout(Layout layout)
    {
        _layout = layout;
        LayoutAll(false);
    }

    /// <summary>Reuses pooled tile nodes for the next forge. Zero allocations per frame.</summary>
    public void ResetFor(ForgeSession session, int shuffleSeed)
    {
        _letters = session.ShuffledTiles(shuffleSeed);
        _used = new bool[_letters.Length];
        _selection.Clear();
        _dragging = false;
        _gestureAdds = 0;
        _lastTapTile = -1;
        _nudgeTile = -1;
        _rejectDot = 0f;
        HideRevealOffer();

        while (_tiles.Count < _letters.Length)
        {
            var tile = new RackTile { TileIndex = _tiles.Count };
            AddChild(tile);
            MoveChild(tile, _actions.GetIndex());
            _tiles.Add(tile);
        }

        for (int i = 0; i < _tiles.Count; i++)
        {
            bool visible = i < _letters.Length;
            _tiles[i].Visible = visible;
            if (!visible) continue;
            _tiles[i].TileIndex = i;
            _tiles[i].SetState(RackTile.TileState.Rest, false);
            _tiles[i].SetSelectionOrder(0);
            _tiles[i].SetGlow(0f);
            _tiles[i].SetLetter(_letters[i]);
            _tiles[i].SetState(_letters[i] == '*' ? RackTile.TileState.Wild : RackTile.TileState.Rest, false);
        }

        EmitCurrentWord();
        LayoutAll(false);
    }

    // ---------------------------------------------------------------- layout
    private void LayoutAll(bool animate, bool redeal = false)
    {
        if (Size.X < 1f || _letters.Length == 0) return;

        float w = Size.X;
        float h = Size.Y;
        float gap = Tok.Dp(8f);
        float inset = Tok.Dp(Tok.BottomInsetDp);
        float actionH = Tok.Dp(_layout.ActionDp);
        float stripH = Tok.Dp(_layout.ComposeDp);
        float revealH = _revealOffer ? Tok.Dp(44f) + gap : 0f;

        _strip.Position = new Vector2(0, 0);
        _strip.Size = new Vector2(w, stripH);

        // The compose strip is the one place the ladder can out-run the type: a
        // 34 sp word in a 36 dp row is 4 dp taller than its own box on every
        // device, and a Label cannot be squeezed below its line box. Fit the type
        // to the strip instead of letting it overflow into the tiles.
        _wordLabel.AddThemeFontSizeOverride("font_size", Tok.I(Mathf.Min(34f, _layout.ComposeDp * 0.72f)));

        float hintH = stripH - Tok.Dp(2f);
        _hintButton.CustomMinimumSize = new Vector2(Tok.Dp(_hintLabel.Text == "FREE" ? 64f : 48f), hintH);
        _hintButton.Size = new Vector2(Tok.Dp(_hintLabel.Text == "FREE" ? 64f : 48f), hintH);
        _hintButton.Position = new Vector2(w - _hintButton.Size.X, (stripH - hintH) * 0.5f);

        _wordLabel.Position = new Vector2(0, 0);
        _wordLabel.Size = new Vector2(w - _hintButton.Size.X - Tok.Dp(8f), stripH);

        // The action row is exactly the ladder height, and it sits ON the
        // gesture-nav inset. A fixed 72 dp button ignored the ladder and pushed
        // the row 8 dp past the bottom of the screen on the Compact breakpoint.
        _actions.Position = new Vector2(0, h - inset - actionH);
        _actions.Size = new Vector2(w, actionH);
        _clearButton.CustomMinimumSize = new Vector2(Tok.Dp(56f), actionH);
        _forgeButton.CustomMinimumSize = new Vector2(0, actionH);
        _shuffleButton.CustomMinimumSize = new Vector2(actionH, actionH);

        _revealButton.Visible = _revealOffer;
        if (_revealOffer)
        {
            _revealButton.Position = new Vector2(0, h - inset - actionH - gap - Tok.Dp(44f));
            _revealButton.Size = new Vector2(w, Tok.Dp(44f));
        }

        // Rack geometry. Rows are capped at 4 per row; a 5+ tile bank splits into
        // two rows so the tool never shrinks below the 48 dp target floor.
        int count = _letters.Length;
        int rows = count <= 4 ? 1 : 2;
        float rackBottom = h - inset - actionH - gap - revealH;
        float availH = rackBottom - stripH - gap;
        float availW = w;

        int topRow = rows == 1 ? count : count / 2;
        int bottomRow = count - topRow;
        int widest = Math.Max(topRow, bottomRow);
        float tile = Mathf.Min(Tok.Dp(_layout.RackTileDp), (availW - gap * (widest - 1)) / Mathf.Max(1, widest));
        tile = Mathf.Min(tile, (availH - gap * (rows - 1)) / rows);
        tile = Mathf.Max(tile, Tok.Dp(48f));
        _tilePx = tile;

        float rowStep = tile + gap;
        int index = 0;
        for (int row = 0; row < rows; row++)
        {
            int inRow = row == 0 ? topRow : bottomRow;
            float y = rackBottom - tile - (rows - 1 - row) * rowStep;
            float rowWidth = inRow * tile + (inRow - 1) * gap;
            float x = (availW - rowWidth) * 0.5f;

            for (int i = 0; i < inRow; i++)
            {
                RackTile tileNode = _tiles[index];
                tileNode.SetDiameter(tile);

                // A gentle arc: tiles are held in a hand, not laid on a shelf.
                float t = inRow <= 1 ? 0.5f : i / (float)(inRow - 1);
                float bow = Mathf.Sin(t * Mathf.Pi) * Tok.Dp(4f);
                float yOffset = row == 0 ? -bow : bow * 0.5f;

                var target = new Vector2(x, y + yOffset);
                tileNode.Place(target, animate, redeal ? index * 0.04f : 0f);
                x += tile + gap;
                index++;
            }
        }

        QueueRedraw();
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) LayoutAll(false);
    }

    // ------------------------------------------------------------ selection
    private Vector2 ToGlobal(Vector2 position)
        => GetGlobalRect().HasPoint(position) ? position : GetGlobalTransform() * position;

    private int TileIndexAt(Vector2 globalPosition)
    {
        for (int i = 0; i < _letters.Length; i++)
        {
            if (!_tiles[i].Visible) continue;
            if (_tiles[i].GetGlobalRect().Grow(Tok.Dp(2f)).HasPoint(globalPosition)) return i;
        }
        return -1;
    }

    private bool AddTileAt(Vector2 rawPosition)
    {
        int index = TileIndexAt(ToGlobal(rawPosition));
        if (index < 0 || _used[index]) return false;
        if (_selection.Count > 0 && _selection[^1] == index) return false;

        _used[index] = true;
        _selection.Add(index);
        _gestureAdds++;

        RackTile tile = _tiles[index];
        tile.SetState(RackTile.TileState.Selected);
        tile.SetSelectionOrder(_selection.Count);
        EmitCurrentWord();
        QueueRedraw();

        // Magnitude = 1-based letter index: spelling a word becomes a melody.
        EventBus.Cue("tile.pick", _selection.Count);
        UiAnim.Haptic(8);
        EmitSignal(SignalName.TilePicked, index);
        return true;
    }

    /// <summary>
    /// Drops the current selection. Deliberately SILENT: this runs after every
    /// submission and on a new gesture, so a cue here would fire a phantom undo
    /// on each tap (AUDIO_DESIGN section 4.5).
    /// </summary>
    public void ClearSelection()
    {
        if (_selection.Count == 0) return;
        foreach (int index in _selection) ReleaseTile(index);
        _selection.Clear();
        EmitCurrentWord();
        QueueRedraw();
    }

    private void ReleaseTile(int index)
    {
        _used[index] = false;
        RackTile tile = _tiles[index];
        tile.SetSelectionOrder(0);
        tile.SetState(_letters[index] == '*' ? RackTile.TileState.Wild : RackTile.TileState.Rest);
    }

    public void RemoveLast()
    {
        if (_selection.Count == 0) return;
        int index = _selection[^1];
        _selection.RemoveAt(_selection.Count - 1);
        ReleaseTile(index);
        EmitCurrentWord();
        QueueRedraw();
        EventBus.Cue("tile.undo");
    }

    private string BuildWord()
    {
        if (_selection.Count == 0) return "";
        char[] buffer = new char[_selection.Count];
        for (int i = 0; i < _selection.Count; i++) buffer[i] = _letters[_selection[i]];
        return new string(buffer);
    }

    private void EmitCurrentWord()
    {
        string word = BuildWord();
        // UI-61: the empty strip renders NOTHING - no placeholder, no tutorial
        // copy. The strip is reserved space so the rack never shifts.
        _wordLabel.Text = word.Length == 0 ? "" : word.ToUpperInvariant();
        _wordLabel.AddThemeColorOverride("font_color", word.Length == 0 ? Tok.Gold : Tok.Gold);
        SetForgeEnabled(word.Length >= MinWordLength);
    }

    /// <summary>
    /// A disabled primary must not vanish and must not read as a broken promise:
    /// it stays in place at 40% with a Muted label (section 7.2).
    /// </summary>
    private void SetForgeEnabled(bool enabled)
    {
        if (_forgeButton is null) return;
        // The disabled LOOK lives in the stylebox (Ui.Style). Modulating here as
        // well multiplied the two dimmings together and made the label illegible.
        _forgeButton.Disabled = !enabled;
    }

    public void SubmitCurrent()
    {
        string word = BuildWord();
        if (word.Length < MinWordLength) return;
        EmitSignal(SignalName.WordSubmitted, word);
    }

    /// <summary>Unlimited free shuffle (GDD 3.1). Selection drops, letters stay.</summary>
    public void Shuffle()
    {
        if (_letters.Length == 0) return;
        var rng = new Random(System.Environment.TickCount);
        for (int i = _letters.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_letters[i], _letters[j]) = (_letters[j], _letters[i]);
        }
        Array.Clear(_used);
        _selection.Clear();
        for (int i = 0; i < _letters.Length; i++)
        {
            _tiles[i].SetSelectionOrder(0);
            _tiles[i].SetState(_letters[i] == '*' ? RackTile.TileState.Wild : RackTile.TileState.Rest, false);
            _tiles[i].SetLetter(_letters[i]);
        }
        EmitCurrentWord();
        LayoutAll(true, redeal: true);
        QueueRedraw();
        EventBus.Cue("tile.shuffle");
        EmitSignal(SignalName.ShuffleRequested);
    }

    // -------------------------------------------------------------- feedback
    /// <summary>
    /// UI-P2: rejection is information, not punishment. The rack and the strip
    /// wobble 6 dp for 90 ms, the strip text turns Rust, a 6 dp dot fades above
    /// the strip. The grid does not move. The screen does not shake.
    /// </summary>
    public void Reject()
    {
        _wordLabel.AddThemeColorOverride("font_color", Tok.RustText);
        _rejectDot = 1f;
        EventBus.Cue("word.reject");
        UiAnim.Haptic(10);

        Tween tween = UiAnim.On(this, "wobble");
        tween.TweenMethod(Callable.From<float>(ApplyWobble), 0f, 1f, UiAnim.Dur(Tok.TReject));
        tween.TweenCallback(Callable.From(() =>
        {
            ApplyWobble(0f);
            _wordLabel.AddThemeColorOverride("font_color", Tok.Gold);
        }));

        Tween fade = UiAnim.On(this, "rejectdot");
        fade.TweenInterval(0.24);
        fade.TweenMethod(Callable.From<float>(v => { _rejectDot = v; QueueRedraw(); }), 1f, 0f, 0.24);
    }

    private void ApplyWobble(float t)
    {
        float wave = t <= 0f ? 0f : Mathf.Sin(t * Mathf.Pi * 4f) * Mathf.Exp(-4f * t) * Tok.Dp(6f);
        for (int i = 0; i < _letters.Length; i++)
        {
            RackTile tile = _tiles[i];
            tile.Position = new Vector2(tile.BasePosition.X + wave,
                                        tile.BasePosition.Y + Tok.Dp(tile.State is RackTile.TileState.Selected ? -6f : 0f));
        }
        _strip.Position = new Vector2(wave, 0);
    }

    /// <summary>
    /// UI-36: the free nudge is framed as the player's own perception. The letter
    /// breathes in place on the rack - no toast, no "free!" label.
    /// </summary>
    public void HighlightLetter(char letter)
    {
        char upper = char.ToUpperInvariant(letter);
        _nudgeTile = -1;
        for (int i = 0; i < _letters.Length; i++)
        {
            bool match = char.ToUpperInvariant(_letters[i]) == upper;
            if (match) { _nudgeTile = i; _tiles[i].SetState(RackTile.TileState.Nudge); }
            else if (!_used[i]) _tiles[i].SetState(_letters[i] == '*' ? RackTile.TileState.Wild : RackTile.TileState.Rest);
        }
        _nudgePhase = 0f;
    }

    public void ClearNudge()
    {
        if (_nudgeTile < 0) return;
        for (int i = 0; i < _letters.Length; i++)
        {
            _tiles[i].SetGlow(0f);
            if (!_used[i])
                _tiles[i].SetState(_letters[i] == '*' ? RackTile.TileState.Wild : RackTile.TileState.Rest);
        }
        _nudgeTile = -1;
    }

    public void SetHint(int remaining, bool free)
    {
        bool show = remaining > 0 || free;
        _hintButton.Visible = show;
        if (!show) return;
        _hintLabel.Text = free ? "FREE" : remaining.ToString();
        LayoutAll(false);
    }

    public void ShowRevealOffer(Action onReveal)
    {
        _onReveal = onReveal;
        _revealOffer = true;
        _revealGeneration++;
        int generation = _revealGeneration;
        LayoutAll(true);

        // Declining requires no action: it fades after 6 s (UI-37).
        HideRevealAfter(generation);
    }

    private async void HideRevealAfter(int generation)
    {
        await ToSignal(GetTree().CreateTimer(6.0, false), SceneTreeTimer.SignalName.Timeout);
        if (generation != _revealGeneration) return;
        HideRevealOffer();
    }

    public void HideRevealOffer()
    {
        if (!_revealOffer) return;
        _revealOffer = false;
        _revealGeneration++;
        LayoutAll(true);
    }

    private void TakeRevealOffer()
    {
        Action? reveal = _onReveal;
        HideRevealOffer();
        reveal?.Invoke();
    }

    public override void _Process(double delta)
    {
        if (_nudgeTile < 0) return;
        // The ONLY looping animation in the game (UI_VISUAL_SYSTEM section 1).
        // Reduced motion turns it into a steady glow instead of a pulse.
        _nudgePhase += (float)delta;
        float glow = UiAnim.Reduced
            ? 0.6f
            : 0.5f + 0.5f * Mathf.Sin(_nudgePhase * Mathf.Tau / 1.2f);
        _tiles[_nudgeTile].SetGlow(0.25f + 0.75f * glow);
    }

    /// <summary>
    /// The rack draws only two things itself, both BEHIND the tiles: the drag
    /// line (UI-33) and the rejection dot. Children draw on top, which is
    /// exactly the layer order a selection line needs.
    /// </summary>
    public override void _Draw()
    {
        if (_selection.Count >= 2)
        {
            var points = new Vector2[_selection.Count];
            for (int i = 0; i < _selection.Count; i++)
            {
                RackTile tile = _tiles[_selection[i]];
                points[i] = tile.Position + tile.Size * 0.5f;
            }
            // 6 dp line + 18 dp glow: a thumb covers the tiles it selects, so the
            // LINE is what the player reads mid-gesture.
            DrawPolyline(points, Tok.A(Tok.Gold, 0.30f), Tok.Dp(18f), true);
            DrawPolyline(points, Tok.A(Tok.GoldPale, 0.80f), Tok.Dp(6f), true);
        }

        if (_rejectDot > 0.01f)
        {
            var dot = new Vector2(Size.X * 0.5f, Tok.Dp(_layout.ComposeDp) + Tok.Dp(4f));
            DrawCircle(dot, Mathf.Max(1.5f, Tok.Dp(3f)), Tok.A(Tok.Rust, _rejectDot));
        }
    }

    // ----------------------------------------------------------------- input
    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventScreenTouch touch)
        {
            if (touch.Pressed) BeginGesture(touch.Position);
            else if (_dragging) EndGesture();
            AcceptEvent();
            return;
        }
        if (@event is InputEventScreenDrag drag && _dragging)
        {
            AddTileAt(drag.Position);
            AcceptEvent();
            return;
        }
        if (@event is InputEventMouseButton mouse && mouse.ButtonIndex == MouseButton.Left)
        {
            if (mouse.Pressed) BeginGesture(mouse.Position);
            else if (_dragging) EndGesture();
            AcceptEvent();
            return;
        }
        if (@event is InputEventMouseMotion motion && _dragging && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            AddTileAt(motion.Position);
            AcceptEvent();
        }
    }

    private void BeginGesture(Vector2 position)
    {
        _dragging = true;
        _gestureAdds = 0;

        int index = TileIndexAt(ToGlobal(position));
        if (index < 0) return;

        // UI-34: double-tap the last tile to submit.
        double now = Time.GetTicksMsec() / 1000.0;
        bool isLastSelected = _selection.Count > 0 && _selection[^1] == index;
        if (isLastSelected && _lastTapTile == index && now - _lastTapAt < 0.4)
        {
            if (_selection.Count >= MinWordLength) SubmitCurrent();
            _lastTapAt = now;
            return;
        }
        _lastTapTile = index;
        _lastTapAt = now;

        ClearNudge();
        AddTileAt(position);
    }

    private void EndGesture()
    {
        _dragging = false;
        // A drag of 2+ tiles is an intentional word: submit on release.
        // Taps keep building, so tap-to-spell players are not forced to drag.
        if (_gestureAdds >= 2 && _selection.Count >= MinWordLength) SubmitCurrent();
        _gestureAdds = 0;
    }
}
