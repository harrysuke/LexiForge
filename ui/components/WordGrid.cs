using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// Crossword-style slot view. Cells are pooled in one GridContainer and reused
/// by ResetFor; Refresh() only rewrites Text/Colours, never allocates nodes.
/// Intersection cells use the ember accent so the >=2 shared cells required by
/// the GDD 3.2 content contract are visible to the player.
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

    private GridContainer _grid = null!;
    private readonly List<Label> _cells = new();
    private readonly List<CellBinding> _bindings = new();
    private int[] _bestBinding = Array.Empty<int>();
    private ForgeSession? _session;

    public override void _Ready()
    {
        var frame = Ui.MakePanel(Ui.Panel);
        AddChild(frame);
        Ui.FullRect(frame);

        // CenterContainer keeps the grid at its combined minimum size and centres
        // it inside the panel; a bare GridContainer would pin every cell to the
        // top-left of a 1080x1920 portrait screen.
        var centre = new CenterContainer();
        frame.AddChild(centre);

        _grid = new GridContainer();
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        centre.AddChild(_grid);
    }

    public void ResetFor(ForgeSession session)
    {
        _session = session;
        _bindings.Clear();

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

        int columns = Math.Max(1, maxColumn + 1);
        int rows = Math.Max(1, maxRow + 1);
        _grid.Columns = columns;

        int needed = rows * columns;
        while (_cells.Count < needed)
        {
            var label = Ui.MakeLabel("", 40, Ui.Text);
            label.CustomMinimumSize = new Vector2(96, 96);
            label.MouseFilter = MouseFilterEnum.Ignore;
            // Label supports a "normal" StyleBox: without it, masked slots are
            // invisible and the crossword structure cannot be read on device.
            label.AddThemeStyleboxOverride("normal", Ui.Box(Ui.PanelRaised, 12));
            _grid.AddChild(label);
            _cells.Add(label);
        }
        for (int i = 0; i < _cells.Count; i++) _cells[i].Visible = i < needed;

        for (int i = 0; i < flat.Count; i++)
        {
            var item = flat[i];
            _bindings.Add(new CellBinding(
                item.Row * columns + item.Column,
                item.Slot,
                item.LetterIndex,
                shared.Contains(i),
                session.Forge.Slots[item.Slot].IsKeystone));
        }
        _bestBinding = new int[needed];
        Refresh();
    }

    /// <summary>
    /// An intersection cell is claimed by two slots. Both words share the letter,
    /// but only one slot may be solved/revealed, so pick the binding that shows
    /// the player the most information instead of letting "last write wins" hide
    /// a revealed letter.
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

        for (int i = 0; i < _cells.Count; i++)
        {
            _cells[i].Text = "";
            _cells[i].Modulate = new Color(1, 1, 1, 0);
        }

        Array.Fill(_bestBinding, -1);
        for (int i = 0; i < _bindings.Count; i++)
        {
            CellBinding b = _bindings[i];
            if (b.CellIndex < 0 || b.CellIndex >= _bestBinding.Length) continue;
            int incumbent = _bestBinding[b.CellIndex];
            if (incumbent < 0 || InformationScore(b) > InformationScore(_bindings[incumbent]))
                _bestBinding[b.CellIndex] = i;
        }

        for (int cell = 0; cell < _bestBinding.Length; cell++)
        {
            int chosen = _bestBinding[cell];
            if (chosen < 0) continue;
            CellBinding binding = _bindings[chosen];

            bool locked = binding.IsKeystone && !_session.KeystoneUnlocked;
            bool filled = _session.IsSlotFilled(binding.SlotIndex);
            string mask = _session.MaskFor(binding.SlotIndex);
            char c = binding.LetterIndex < mask.Length ? mask[binding.LetterIndex] : '_';

            Label label = _cells[cell];
            label.Modulate = Colors.White;
            label.Text = locked ? "?" : (c == '_' ? "" : c.ToString());

            Color color = filled ? Ui.Good
                        : binding.IsIntersection ? Ui.Ember
                        : Ui.Text;
            label.AddThemeColorOverride("font_color", locked ? Ui.Muted : color);
        }
    }
}
