using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The Solve band: a crossword of carved molds that fill in.
///
/// Cells are pooled in one absolutely-positioned layer and reused by ResetFor;
/// Refresh() only rewrites content and lock animations, never allocates nodes.
///
/// Two things this view does that a uniform GridContainer cannot:
///  - **Letter counts** (UI-28) run along each empty slot's own axis, so the gap
///    is legible before the player has typed anything.
///  - **The keystone gets breathing room** (UI-27). Its cells are drawn with a
///    different silhouette (radius 14 + notch + gold rim) AND the layout inserts
///    extra dp before its row and column. The keystone always intersects the
///    crossword, so it can never be physically detached - the silhouette is what
///    survives greyscale, which is the point of the Von Restorff rule.
/// </summary>
public sealed partial class WordGrid : Control
{
    private readonly struct CellBinding
    {
        public readonly int CellIndex;
        public readonly int SlotIndex;
        public readonly int LetterIndex;
        public readonly bool IsIntersection;
        public readonly bool IsKeystone;

        public CellBinding(int cellIndex, int slotIndex, int letterIndex, bool isIntersection, bool isKeystone)
        {
            CellIndex = cellIndex;
            SlotIndex = slotIndex;
            LetterIndex = letterIndex;
            IsIntersection = isIntersection;
            IsKeystone = isKeystone;
        }
    }

    private readonly List<SlotCell> _cells = new();
    private readonly List<CellBinding> _bindings = new();
    private readonly Dictionary<int, bool> _wasFilled = new();
    private int[] _best = Array.Empty<int>();
    private ForgeSession? _session;
    private Layout _layout = Layout.For(1920f, 1080f);
    private int _columns = 1;
    private float _cellPx;
    private Rect2 _gridRect;

    private Control _sweepLayer = null!;
    private ColorRect _sweepBand = null!;
    private Vector2? _lastFilledGlobal;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;

        // Sweep lives above the cells and is clipped to the grid rect, so the
        // gold band never leaks over the HUD or the rack.
        _sweepLayer = new Control
        {
            MouseFilter = MouseFilterEnum.Ignore,
            ClipContents = true
        };
        AddChild(_sweepLayer);
        Ui.FullRect(_sweepLayer);

        _sweepBand = new ColorRect
        {
            Color = Tok.A(Tok.GoldPale, 0.30f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false
        };
        _sweepLayer.AddChild(_sweepBand);
    }

    public void ApplyLayout(Layout layout)
    {
        _layout = layout;
        LayoutCells();
    }

    public void ResetFor(ForgeSession session)
    {
        _session = session;
        _bindings.Clear();
        _wasFilled.Clear();
        _lastFilledGlobal = null;

        // First pass: bounding box + which slot/letter owns each grid coordinate.
        int maxRow = 0, maxColumn = 0;
        var owners = new Dictionary<(int Row, int Column), int>();
        var flat = new List<(int Row, int Column, int Slot, int LetterIndex)>();
        var shared = new HashSet<int>();

        for (int s = 0; s < session.Forge.Slots.Count; s++)
        {
            GridSlot slot = session.Forge.Slots[s];
            int letterIndex = 0;
            foreach (var cell in slot.Cells())
            {
                var key = (cell.Row, cell.Column);
                int index = flat.Count;
                if (owners.TryGetValue(key, out int first))
                {
                    shared.Add(first);
                    shared.Add(index);
                }
                else owners[key] = index;

                flat.Add((cell.Row, cell.Column, s, letterIndex++));
                maxRow = Math.Max(maxRow, cell.Row);
                maxColumn = Math.Max(maxColumn, cell.Column);
            }
        }

        _columns = Math.Max(1, maxColumn + 1);
        int rows = Math.Max(1, maxRow + 1);
        int needed = rows * _columns;

        while (_cells.Count < needed)
        {
            var cell = new SlotCell { MouseFilter = MouseFilterEnum.Ignore };
            AddChild(cell);
            MoveChild(cell, _sweepLayer.GetIndex());
            _cells.Add(cell);
        }
        for (int i = 0; i < _cells.Count; i++) _cells[i].Visible = i < needed;

        for (int i = 0; i < flat.Count; i++)
        {
            var item = flat[i];
            _bindings.Add(new CellBinding(
                item.Row * _columns + item.Column,
                item.Slot,
                item.LetterIndex,
                shared.Contains(i),
                session.Forge.Slots[item.Slot].IsKeystone));
        }
        _best = new int[needed];

        LayoutCells();
        Refresh();
    }

    // ---------------------------------------------------------------- layout
    private void LayoutCells()
    {
        if (_session is null || _cells.Count == 0) return;

        float availableW = Size.X;
        float availableH = Size.Y;
        if (availableW < 1f || availableH < 1f) return;

        int rows = _best.Length / Math.Max(1, _columns);
        if (rows < 1) rows = 1;

        var keystoneColumns = new bool[_columns];
        var keystoneRows = new bool[rows];
        foreach (CellBinding binding in _bindings)
        {
            if (!binding.IsKeystone) continue;
            int row = binding.CellIndex / _columns;
            int col = binding.CellIndex % _columns;
            if (row >= 0 && row < rows) keystoneRows[row] = true;
            if (col >= 0 && col < _columns) keystoneColumns[col] = true;
        }

        float gap = Tok.Dp(6f);
        float keystoneGap = Tok.Dp(14f);

        float cell = Mathf.Min(Tok.Dp(_layout.SlotDp),
                              (availableW - gap * (_columns - 1)) / _columns);
        cell = Mathf.Min(cell, (availableH - gap * (rows - 1)) / rows);
        cell = Mathf.Max(cell, Tok.Dp(40f));
        _cellPx = cell;

        // Cumulative positions, with extra breathing room before a keystone
        // row/column (UI-27).
        var colX = new float[_columns];
        float x = 0f;
        for (int c = 0; c < _columns; c++)
        {
            colX[c] = x;
            x += cell + (c < _columns - 1 ? gap + (keystoneColumns[c + 1] ? keystoneGap : 0f) : 0f);
        }
        float totalW = x;

        var rowY = new float[rows];
        float y = 0f;
        for (int r = 0; r < rows; r++)
        {
            rowY[r] = y;
            y += cell + (r < rows - 1 ? gap + (keystoneRows[r + 1] ? keystoneGap : 0f) : 0f);
        }
        float totalH = y;

        float originX = (availableW - totalW) * 0.5f;
        float originY = (availableH - totalH) * 0.5f;
        _gridRect = new Rect2(originX, originY, totalW, totalH);

        for (int index = 0; index < _cells.Count; index++)
        {
            SlotCell node = _cells[index];
            if (!node.Visible) continue;
            int row = index / _columns;
            int col = index % _columns;
            node.Position = new Vector2(originX + colX[col], originY + rowY[row]);
            node.Configure(cell, IsKeystoneCell(index), false, false, 0, false);
        }
    }

    private bool IsKeystoneCell(int cellIndex)
    {
        foreach (CellBinding binding in _bindings)
            if (binding.CellIndex == cellIndex && binding.IsKeystone) return true;
        return false;
    }

    // ---------------------------------------------------------------- content
    /// <summary>
    /// An intersection cell is claimed by two slots. Both words share the letter,
    /// but only one slot may be solved/revealed, so pick the binding that shows
    /// the player the most information instead of "last write wins".
    /// </summary>
    private int InformationScore(CellBinding b)
    {
        if (_session is null || b.SlotIndex < 0) return -1;
        string mask = _session.MaskFor(b.SlotIndex);
        char c = b.LetterIndex < mask.Length ? mask[b.LetterIndex] : '_';
        int score = _session.IsSlotFilled(b.SlotIndex) ? 100 : 0;
        if (c != '_') score += 10;
        if (!(b.IsKeystone && !_session.KeystoneUnlocked)) score += 1;
        return score;
    }

    public void Refresh()
    {
        if (_session is null) return;

        Array.Fill(_best, -1);
        for (int i = 0; i < _bindings.Count; i++)
        {
            CellBinding b = _bindings[i];
            if (b.CellIndex < 0 || b.CellIndex >= _best.Length) continue;
            int incumbent = _best[b.CellIndex];
            if (incumbent < 0 || InformationScore(b) > InformationScore(_bindings[incumbent]))
                _best[b.CellIndex] = i;
        }

        for (int cell = 0; cell < _best.Length; cell++)
        {
            int chosen = _best[cell];
            if (chosen < 0 || cell >= _cells.Count) continue;
            CellBinding binding = _bindings[chosen];
            SlotCell node = _cells[cell];

            bool locked = binding.IsKeystone && !_session.KeystoneUnlocked;
            bool filled = _session.IsSlotFilled(binding.SlotIndex);
            string mask = _session.MaskFor(binding.SlotIndex);
            char c = binding.LetterIndex < mask.Length ? mask[binding.LetterIndex] : '_';

            bool lengthVisible = _session.IsSlotLengthVisible(binding.SlotIndex);
            bool firstLetter = binding.LetterIndex == 0;
            int letterCount = _session.Forge.Slots[binding.SlotIndex].Word.Length;

            node.Visible = true;
            node.Configure(_cellPx, binding.IsKeystone,
                           showPips: !filled && !locked && firstLetter && !lengthVisible,
                           showCount: !filled && !locked && firstLetter && lengthVisible,
                           letterCount: letterCount,
                           vertical: _session.Forge.Slots[binding.SlotIndex].IsVertical);
            node.SetContent(c == '_' ? '\0' : c, filled, locked);

            bool wasFilled = _wasFilled.TryGetValue(cell, out bool previous) && previous;
            if (filled && !wasFilled)
            {
                node.PlayLock(binding.IsKeystone);
                _lastFilledGlobal = node.GetGlobalRect().GetCenter();
            }
            _wasFilled[cell] = filled;
        }
    }

    /// <summary>
    /// Global centre of the cell that most recently filled. The bonus coin arcs
    /// from here to the jar, so the reward originates at the object the player
    /// touched (UI_VISUAL_SYSTEM section 1, lever 3).
    /// </summary>
    public Vector2? ConsumeLastFilled()
    {
        Vector2? value = _lastFilledGlobal;
        _lastFilledGlobal = null;
        return value;
    }

    /// <summary>
    /// The keystone's gold sweep: a 40 dp band travelling left→right across the
    /// grid block, 320 ms. The only loud event in a forge (UI-01, section 8.3).
    /// </summary>
    public void PlayKeystoneSweep()
    {
        if (_gridRect.Size.X <= 1f) return;
        float width = Tok.Dp(40f);
        _sweepBand.Size = new Vector2(width, _gridRect.Size.Y);
        _sweepBand.Position = new Vector2(_gridRect.Position.X - width, _gridRect.Position.Y);
        _sweepBand.Visible = true;

        if (UiAnim.Reduced)
        {
            _sweepBand.Visible = false;
            return;
        }

        Tween tween = UiAnim.On(_sweepBand, "sweep");
        tween.TweenProperty(_sweepBand, "position",
                            new Vector2(_gridRect.End.X, _gridRect.Position.Y), 0.32f)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        tween.TweenCallback(Callable.From(() => _sweepBand.Visible = false));
    }
}
