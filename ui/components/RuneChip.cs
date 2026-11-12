using Godot;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// A rune chip (UI_VISUAL_SYSTEM section 6.7). The SAME component appears on
/// Home, on the Draft card and on the Run End screen - one object seen three
/// times reads as ownership, which is the whole point of E4.
///
/// The empty variant is a thin gold outline with a "+", never a dashed border: a
/// dashed box reads as a wireframe, a gold outline reads as "a slot waiting".
/// </summary>
public sealed partial class RuneChip : Control
{
    private RuneData? _rune;
    private bool _empty;
    private IconGlyph _glyph = null!;
    private Label _label = null!;

    public static RuneChip Create(RuneData? rune, bool empty)
    {
        var chip = new RuneChip { _rune = rune, _empty = empty };
        return chip;
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        float height = Tok.Dp(Tok.ChipDp);

        string name = _empty ? "" : _rune?.Name ?? "";
        _label = Ui.MakeLabel(_empty ? "" : name, 20f, _empty ? Tok.Muted : Tok.TextDim, HorizontalAlignment.Left);
        _label.MouseFilter = MouseFilterEnum.Ignore;
        AddChild(_label);

        Glyph glyph = _empty ? Glyph.Plus : FamilyGlyph(_rune?.Kind ?? RuneKind.Utility);
        _glyph = IconGlyph.Create(glyph, 16f, _empty ? Tok.Muted : Tok.GoldPale, 1.8f);
        AddChild(_glyph);

        float textWidth = _empty ? 0f : MeasureText(name);
        float width = Tok.Dp(8f) + Tok.Dp(16f) + Tok.Dp(4f) + textWidth + Tok.Dp(10f);
        if (_empty) width = Tok.Dp(44f);
        CustomMinimumSize = new Vector2(width, height);
        Size = new Vector2(width, height);
        QueueRedraw();
    }

    private float MeasureText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        Font font = GetThemeDefaultFont();
        return font.GetStringSize(text, HorizontalAlignment.Left, -1, Tok.I(20f)).X;
    }

    public static Glyph FamilyGlyph(RuneKind kind) => kind switch
    {
        RuneKind.Score => Glyph.Star,
        RuneKind.Economy => Glyph.Coin,
        _ => Glyph.Mark
    };

    public override void _Notification(int what)
    {
        if (what != NotificationResized) return;
        float h = Size.Y;
        _glyph.Position = new Vector2(Tok.Dp(8f), (h - Tok.Dp(16f)) * 0.5f);
        _label.Position = new Vector2(Tok.Dp(8f) + Tok.Dp(16f) + Tok.Dp(4f), 0);
        _label.Size = new Vector2(Size.X - Tok.Dp(8f) - Tok.Dp(16f) - Tok.Dp(4f) - Tok.Dp(6f), h);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        if (_empty)
        {
            Ui.StrokeRoundRect(this, rect.Grow(-Tok.Dp(0.5f)), 10f, Tok.A(Tok.Gold, 0.30f), 1f);
            return;
        }
        Ui.FillRoundRect(this, rect, 10f, Tok.Surf2);
        Ui.StrokeRoundRect(this, rect, 10f, Tok.LineHair, 1f);
    }
}
