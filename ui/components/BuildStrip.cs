using System.Collections.Generic;
using Godot;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// The rune build strip (UI-23 / UI-41): the visible carrier of E4.
///
/// It shows the current run's runes as chips plus one empty slot when a draft is
/// pending, so the player is COMPLETING A SET rather than picking from a list.
/// The same object appears on Home, on the Draft screen and at Run End.
///
/// Chips are rebuilt on Refresh rather than mutated: this runs once per screen
/// entry (never per frame), and an immutable chip cannot drift out of sync with
/// the run state it is describing.
/// </summary>
public sealed partial class BuildStrip : HBoxContainer
{
    private readonly List<RuneChip> _chips = new();

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", Tok.I(8f));
        MouseFilter = MouseFilterEnum.Ignore;
        SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
    }

    /// <param name="pendingSlot">True on the Draft screen: render one empty "+" chip.</param>
    public void Refresh(IReadOnlyList<RuneData> owned, int maxSlots, bool pendingSlot)
    {
        int slots = Mathf.Clamp(maxSlots, 1, 4);
        int shown = Mathf.Min(owned.Count, slots);
        int wanted = pendingSlot ? Mathf.Min(shown + 1, slots) : shown;

        foreach (RuneChip chip in _chips)
        {
            if (chip.GetParent() == this) RemoveChild(chip);
            chip.QueueFree();
        }
        _chips.Clear();

        for (int i = 0; i < wanted; i++)
        {
            bool isEmpty = pendingSlot && i >= shown;
            RuneData? rune = isEmpty || i >= owned.Count ? null : owned[i];
            RuneChip chip = RuneChip.Create(rune, isEmpty || rune is null);
            AddChild(chip);
            _chips.Add(chip);
        }
    }
}
