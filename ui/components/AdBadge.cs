using Godot;

namespace LexiForge.UI;

/// <summary>
/// The "AD" badge (UI-20 / section 6.7). 28 dp tall, 20 sp text, 10 dp horizontal
/// padding - deliberately the largest compliance label in the genre.
///
/// This is both a Google Play requirement and a trust mechanism: an ad the player
/// chose is a transaction, an ad that surprised them is a betrayal, and betrayal
/// is what drives 1-star reviews.
/// </summary>
public sealed partial class AdBadge : Control
{
    private bool _granted;

    public static AdBadge Create()
    {
        return new AdBadge { MouseFilter = MouseFilterEnum.Ignore };
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        Layout();
        QueueRedraw();
    }

    private void Layout()
    {
        float textWidth = GetThemeDefaultFont()
            .GetStringSize("AD", HorizontalAlignment.Left, -1, Tok.I(20f)).X;
        float width = textWidth + Tok.Dp(20f);
        CustomMinimumSize = new Vector2(width, Tok.Dp(28f));
        Size = new Vector2(width, Tok.Dp(28f));
    }

    public void SetGranted(bool granted)
    {
        if (_granted == granted) return;
        _granted = granted;
        Layout();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var rect = new Rect2(Vector2.Zero, Size);
        Ui.FillRoundRect(this, rect, 6f, Tok.Surf1);
        Ui.StrokeRoundRect(this, rect, 6f, Tok.LineHair, 1f);

        if (_granted)
        {
            // Reward granted: the badge flips to a jade check for 1.2 s. Colour is
            // never the only channel - the glyph changes too (UI-11).
            Vector2 c = rect.GetCenter();
            float s = Tok.Dp(7f);
            DrawLine(c + new Vector2(-s * 0.7f, 0f), c + new Vector2(-s * 0.15f, s * 0.55f),
                     Tok.Jade, Tok.Dp(2f), true);
            DrawLine(c + new Vector2(-s * 0.15f, s * 0.55f), c + new Vector2(s * 0.8f, -s * 0.6f),
                     Tok.Jade, Tok.Dp(2f), true);
            return;
        }

        Font font = GetThemeDefaultFont();
        var textRect = new Rect2(0, 0, Size.X, Size.Y);
        Ui.DrawTextCentred(this, font, "AD", textRect, 20f, Tok.TextDim);
    }
}
