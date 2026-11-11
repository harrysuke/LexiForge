using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Tile rack. Supports BOTH tap-to-build and swipe-to-spell on touch, because
/// forcing one input style on a 1080x1920 portrait device is the fastest way to
/// lose players. Tile Buttons are pooled and reused by ResetFor — no QueueFree
/// between forges (GDD 11.5).
///
/// Input ownership: tile buttons are MouseFilter.Ignore so the rack receives the
/// whole gesture in one _GuiInput. That makes a continuous drag across several
/// tiles possible instead of each button swallowing its own click. Action
/// buttons keep MouseFilter.Stop so they remain independently clickable.
/// </summary>
public sealed partial class LetterRack : Control
{
    [Signal] public delegate void WordSubmittedEventHandler(string word);
    [Signal] public delegate void ShuffleRequestedEventHandler();

    public const int MinWordLength = ForgeSession.MinWordLength;

    private readonly List<Button> _tileButtons = new();
    private readonly List<int> _selection = new();
    private char[] _tiles = Array.Empty<char>();
    private bool[] _used = Array.Empty<bool>();
    private Label _wordLabel = null!;
    private HBoxContainer _tileRow = null!;
    private Button _submitButton = null!;
    private bool _dragging;
    private int _dragTileCount;

    public string CurrentWord => BuildWord();

    /// <summary>Used by the forge view to keep ad loads out of a live gesture (GDD 11.5).</summary>
    public bool IsDragging => _dragging;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;

        VBoxColumnSetup();
        EmitCurrentWord();
    }

    private void VBoxColumnSetup()
    {
        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", 18);
        column.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(column);
        Ui.FullRect(column);

        _wordLabel = Ui.MakeLabel("", 52, Ui.Gold);
        _wordLabel.CustomMinimumSize = new Vector2(0, 96);
        _wordLabel.MouseFilter = MouseFilterEnum.Ignore;
        column.AddChild(_wordLabel);

        _tileRow = new HBoxContainer();
        _tileRow.Alignment = BoxContainer.AlignmentMode.Center;
        _tileRow.AddThemeConstantOverride("separation", 14);
        _tileRow.MouseFilter = MouseFilterEnum.Ignore;
        _tileRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        column.AddChild(_tileRow);

        var actions = Ui.MakeRow(18);
        column.AddChild(actions);
        actions.AddChild(Ui.MakeButton("Shuffle", () => Shuffle(), Ui.PanelRaised, 96, 28));
        actions.AddChild(Ui.MakeButton("Undo", RemoveLast, Ui.PanelRaised, 96, 28));
        _submitButton = Ui.MakeButton("Forge Word", SubmitCurrent, Ui.Ember, 96, 30);
        actions.AddChild(_submitButton);
    }

    /// <summary>Reuses pooled tile nodes for the next forge. Zero allocations per frame.</summary>
    public void ResetFor(ForgeSession session, int shuffleSeed)
    {
        _tiles = session.ShuffledTiles(shuffleSeed);
        _used = new bool[_tiles.Length];
        _selection.Clear();
        _dragging = false;

        while (_tileButtons.Count < _tiles.Length)
        {
            var button = new Button
            {
                CustomMinimumSize = new Vector2(126, 126),
                FocusMode = FocusModeEnum.None,
                MouseFilter = MouseFilterEnum.Ignore
            };
            button.AddThemeFontSizeOverride("font_size", 46);
            button.AddThemeColorOverride("font_color", Ui.Text);
            button.AddThemeStyleboxOverride("normal", Ui.Box(Ui.PanelRaised, 16));
            button.AddThemeStyleboxOverride("hover", Ui.Box(Ui.Ember, 16));
            button.AddThemeStyleboxOverride("pressed", Ui.Box(Ui.Gold, 16));
            button.AddThemeStyleboxOverride("disabled", Ui.Box(Ui.Locked, 16));
            _tileRow.AddChild(button);
            _tileButtons.Add(button);
        }

        for (int i = 0; i < _tileButtons.Count; i++)
        {
            bool visible = i < _tiles.Length;
            _tileButtons[i].Visible = visible;
            if (visible) UpdateTile(i);
        }
        EmitCurrentWord();
    }

    private void UpdateTile(int index)
    {
        var button = _tileButtons[index];
        button.Text = _tiles[index] == '*' ? "★" : char.ToUpperInvariant(_tiles[index]).ToString();
        button.Modulate = _used[index] ? new Color(0.35f, 0.35f, 0.35f, 0.65f) : Colors.White;
    }

    /// <summary>
    /// In Control._GuiInput, mouse events are made LOCAL to the Control while
    /// InputEventScreenTouch/ScreenDrag arrive in viewport coordinates on some
    /// platforms. Everything in this class hit-tests in GLOBAL space, so normalise
    /// here. A point already inside our global rect is viewport-space; otherwise
    /// transform it from local into global.
    /// </summary>
    private Vector2 ToGlobal(Vector2 position)
        => GetGlobalRect().HasPoint(position) ? position : GetGlobalTransform() * position;

    private int TileIndexAt(Vector2 globalPosition)
    {
        for (int i = 0; i < _tiles.Length; i++)
            if (_tileButtons[i].Visible && _tileButtons[i].GetGlobalRect().HasPoint(globalPosition)) return i;
        return -1;
    }

    private void AddTileAt(Vector2 rawPosition)
    {
        int index = TileIndexAt(ToGlobal(rawPosition));
        if (index < 0 || _used[index]) return;
        if (_selection.Count > 0 && _selection[^1] == index) return; // no duplicate while hovering
        _used[index] = true;
        _selection.Add(index);
        _dragTileCount++;
        UpdateTile(index);
        EmitCurrentWord();
    }

    public void ClearSelection()
    {
        foreach (int index in _selection)
        {
            _used[index] = false;
            UpdateTile(index);
        }
        _selection.Clear();
        EmitCurrentWord();
    }

    public void RemoveLast()
    {
        if (_selection.Count == 0) return;
        int index = _selection[^1];
        _selection.RemoveAt(_selection.Count - 1);
        _used[index] = false;
        UpdateTile(index);
        EmitCurrentWord();
    }

    private string BuildWord()
    {
        if (_selection.Count == 0) return "";
        char[] buffer = new char[_selection.Count];
        for (int i = 0; i < _selection.Count; i++) buffer[i] = _tiles[_selection[i]];
        return new string(buffer);
    }

    private void EmitCurrentWord()
    {
        string word = BuildWord();
        _wordLabel.Text = word.Length == 0 ? "Tap or drag letters" : word.ToUpperInvariant();
        _wordLabel.AddThemeColorOverride("font_color", word.Length == 0 ? Ui.Muted : Ui.Gold);
        _submitButton.Disabled = word.Length < MinWordLength;
    }

    public void SubmitCurrent()
    {
        string word = BuildWord();
        if (word.Length < MinWordLength) return;
        EmitSignal(SignalName.WordSubmitted, word);
    }

    /// <summary>Unlimited free shuffle (GDD anti-frustration). Selection is dropped, letters kept.</summary>
    public void Shuffle()
    {
        var rng = new Random(System.Environment.TickCount);
        for (int i = _tiles.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_tiles[i], _tiles[j]) = (_tiles[j], _tiles[i]);
        }
        Array.Clear(_used);
        _selection.Clear();
        for (int i = 0; i < _tiles.Length; i++) UpdateTile(i);
        EmitCurrentWord();
        EmitSignal(SignalName.ShuffleRequested);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventScreenTouch touch)
        {
            if (touch.Pressed)
            {
                ClearSelection();
                _dragging = true;
                _dragTileCount = 0;
                AddTileAt(touch.Position);
            }
            else if (_dragging)
            {
                FinishGesture(_dragTileCount >= 2);
            }
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
            if (mouse.Pressed)
            {
                ClearSelection();
                _dragging = true;
                _dragTileCount = 0;
                AddTileAt(mouse.Position);
            }
            else if (_dragging)
            {
                FinishGesture(_dragTileCount >= 2);
            }
            AcceptEvent();
            return;
        }
        if (@event is InputEventMouseMotion motion && _dragging &&
            Input.IsMouseButtonPressed(MouseButton.Left))
        {
            AddTileAt(motion.Position);
            AcceptEvent();
        }
    }

    private void FinishGesture(bool wasDrag)
    {
        _dragging = false;
        // A drag of 2+ tiles is an intentional word: submit on release.
        // A single tap leaves the letter selected for tap-to-build players.
        if (wasDrag && _selection.Count >= MinWordLength) SubmitCurrent();
        else if (!wasDrag && _selection.Count == 0) EmitCurrentWord();
    }
}
