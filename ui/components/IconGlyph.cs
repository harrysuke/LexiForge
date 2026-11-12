using Godot;

namespace LexiForge.UI;

public enum Glyph
{
    None, Gear, Menu, Pause, Shuffle, Bulb, Coin, Star, Check, Dot, Close, ArrowRight, Plus, Mark, Lock
}

/// <summary>
/// Vector glyphs drawn with primitives instead of font characters.
///
/// Why not `★` / `⚙` / `☰`: the project ships no font binaries, so a glyph that
/// the fallback face lacks renders as a tofu box on a real device. Drawing costs
/// three draw calls, never breaks, and lets every icon inherit its colour from
/// the semantic token that owns it.
/// </summary>
public sealed partial class IconGlyph : Control
{
    private Glyph _glyph = Glyph.None;
    private Color _color = Tok.Gold;
    private float _strokeDp = 2f;

    public static IconGlyph Create(Glyph glyph, float sizeDp, Color color, float strokeDp = 2f)
    {
        return new IconGlyph
        {
            _glyph = glyph,
            _color = color,
            _strokeDp = strokeDp,
            CustomMinimumSize = new Vector2(Tok.Dp(sizeDp), Tok.Dp(sizeDp)),
            Size = new Vector2(Tok.Dp(sizeDp), Tok.Dp(sizeDp)),
            MouseFilter = MouseFilterEnum.Ignore
        };
    }

    public void SetGlyph(Glyph glyph)
    {
        if (_glyph == glyph) return;
        _glyph = glyph;
        QueueRedraw();
    }

    public void SetColor(Color color)
    {
        _color = color;
        QueueRedraw();
    }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_glyph == Glyph.None) return;

        Vector2 size = Size;
        Vector2 centre = size * 0.5f;
        float r = Mathf.Min(size.X, size.Y) * 0.5f;
        float stroke = Mathf.Max(1f, Tok.Dp(_strokeDp));
        Color color = _color;

        switch (_glyph)
        {
            case Glyph.Menu:
                for (int i = -1; i <= 1; i++)
                {
                    float y = centre.Y + i * r * 0.42f;
                    DrawLine(new Vector2(centre.X - r * 0.62f, y), new Vector2(centre.X + r * 0.62f, y), color, stroke, true);
                }
                break;

            case Glyph.Gear:
                DrawArc(centre, r * 0.34f, 0, Mathf.Tau, 20, color, stroke, true);
                for (int i = 0; i < 6; i++)
                {
                    float a = i * Mathf.Tau / 6f;
                    var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    DrawLine(centre + dir * r * 0.42f, centre + dir * r * 0.78f, color, stroke, true);
                }
                break;

            case Glyph.Pause:
                DrawLine(new Vector2(centre.X - r * 0.28f, centre.Y - r * 0.5f),
                         new Vector2(centre.X - r * 0.28f, centre.Y + r * 0.5f), color, stroke * 1.6f, true);
                DrawLine(new Vector2(centre.X + r * 0.28f, centre.Y - r * 0.5f),
                         new Vector2(centre.X + r * 0.28f, centre.Y + r * 0.5f), color, stroke * 1.6f, true);
                break;

            case Glyph.Shuffle:
            {
                // Two paths that cross, each ending in an arrowhead: the read is
                // "these letters go somewhere else", not "cancel" (which is what a
                // bare X means everywhere else in this UI).
                var fromLow = new Vector2(centre.X - r * 0.66f, centre.Y + r * 0.42f);
                var toHigh = new Vector2(centre.X + r * 0.66f, centre.Y - r * 0.42f);
                var fromHigh = new Vector2(centre.X - r * 0.66f, centre.Y - r * 0.42f);
                var toLow = new Vector2(centre.X + r * 0.66f, centre.Y + r * 0.42f);

                DrawLine(fromLow, toHigh, color, stroke, true);
                DrawLine(fromHigh, toLow, color, stroke, true);

                ArrowHead(toHigh, (toHigh - fromLow).Normalized(), r * 0.3f, color, stroke);
                ArrowHead(toLow, (toLow - fromHigh).Normalized(), r * 0.3f, color, stroke);
                break;
            }

            case Glyph.Bulb:
                DrawArc(centre + new Vector2(0, -r * 0.16f), r * 0.44f, 0, Mathf.Tau, 20, color, stroke, true);
                DrawLine(new Vector2(centre.X - r * 0.22f, centre.Y + r * 0.42f),
                         new Vector2(centre.X + r * 0.22f, centre.Y + r * 0.42f), color, stroke, true);
                DrawLine(new Vector2(centre.X - r * 0.16f, centre.Y + r * 0.66f),
                         new Vector2(centre.X + r * 0.16f, centre.Y + r * 0.66f), color, stroke, true);
                break;

            case Glyph.Coin:
                DrawArc(centre, r * 0.62f, 0, Mathf.Tau, 24, color, stroke, true);
                DrawArc(centre, r * 0.3f, 0, Mathf.Tau, 16, Tok.A(color, 0.6f), stroke * 0.8f, true);
                break;

            case Glyph.Mark:
            {
                Vector2[] diamond =
                {
                    centre + new Vector2(0, -r * 0.7f), centre + new Vector2(r * 0.55f, 0),
                    centre + new Vector2(0, r * 0.7f), centre + new Vector2(-r * 0.55f, 0)
                };
                DrawPolyline(new[] { diamond[0], diamond[1], diamond[2], diamond[3], diamond[0] }, color, stroke, true);
                break;
            }

            case Glyph.Star:
                DrawColoredPolygon(StarPoints(centre, r * 0.82f, r * 0.34f), color);
                break;

            case Glyph.Check:
                DrawLine(new Vector2(centre.X - r * 0.5f, centre.Y + r * 0.02f),
                         new Vector2(centre.X - r * 0.12f, centre.Y + r * 0.42f), color, stroke * 1.4f, true);
                DrawLine(new Vector2(centre.X - r * 0.12f, centre.Y + r * 0.42f),
                         new Vector2(centre.X + r * 0.55f, centre.Y - r * 0.42f), color, stroke * 1.4f, true);
                break;

            case Glyph.Dot:
                DrawCircle(centre, Mathf.Max(1.5f, r * 0.34f), color);
                break;

            case Glyph.Close:
                DrawLine(centre + new Vector2(-r * 0.5f, -r * 0.5f), centre + new Vector2(r * 0.5f, r * 0.5f), color, stroke, true);
                DrawLine(centre + new Vector2(-r * 0.5f, r * 0.5f), centre + new Vector2(r * 0.5f, -r * 0.5f), color, stroke, true);
                break;

            case Glyph.ArrowRight:
                DrawLine(new Vector2(centre.X - r * 0.6f, centre.Y), new Vector2(centre.X + r * 0.5f, centre.Y), color, stroke, true);
                DrawLine(new Vector2(centre.X + r * 0.1f, centre.Y - r * 0.36f),
                         new Vector2(centre.X + r * 0.55f, centre.Y), color, stroke, true);
                DrawLine(new Vector2(centre.X + r * 0.1f, centre.Y + r * 0.36f),
                         new Vector2(centre.X + r * 0.55f, centre.Y), color, stroke, true);
                break;

            case Glyph.Plus:
                DrawLine(new Vector2(centre.X - r * 0.45f, centre.Y), new Vector2(centre.X + r * 0.45f, centre.Y), color, stroke, true);
                DrawLine(new Vector2(centre.X, centre.Y - r * 0.45f), new Vector2(centre.X, centre.Y + r * 0.45f), color, stroke, true);
                break;

            case Glyph.Lock:
                DrawArc(centre + new Vector2(0, -r * 0.18f), r * 0.32f, Mathf.Pi, Mathf.Tau, 16, color, stroke, true);
                Ui.FillRoundRect(this, new Rect2(centre.X - r * 0.5f, centre.Y - r * 0.1f, r, r * 0.72f), 2f, color);
                break;
        }
    }

    /// <summary>Two short strokes at the tip of a direction vector.</summary>
    private void ArrowHead(Vector2 tip, Vector2 direction, float length, Color color, float stroke)
    {
        float angle = Mathf.Atan2(direction.Y, direction.X);
        for (int side = -1; side <= 1; side += 2)
        {
            float a = angle + Mathf.Pi + side * 0.62f;
            DrawLine(tip, tip + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * length, color, stroke, true);
        }
    }

    /// <summary>Five-point star polygon, flat-top up. Used by the results card and jar burst.</summary>
    public static Vector2[] StarPoints(Vector2 centre, float outer, float inner)
    {
        var points = new Vector2[10];
        for (int i = 0; i < 10; i++)
        {
            float radius = i % 2 == 0 ? outer : inner;
            float angle = -Mathf.Pi * 0.5f + i * Mathf.Pi / 5f;
            points[i] = centre + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
        }
        return points;
    }
}
