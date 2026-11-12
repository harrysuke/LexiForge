using Godot;

namespace LexiForge.UI;

/// <summary>
/// A results-card star (UI-49). Stars animate ONE AT A TIME, 180 ms apart, because
/// three stars must feel like three events, not one.
///
/// An unearned star renders as a 1 dp outline, never a grey star: an unfilled
/// slot is a goal, a grey star is a verdict (section 7.4).
/// </summary>
public sealed partial class StarIcon : Control
{
    private bool _filled;
    private float _scale = 1f;

    public static StarIcon Create(float sizeDp)
    {
        var star = new StarIcon { _filled = false };
        star.CustomMinimumSize = new Vector2(Tok.Dp(sizeDp), Tok.Dp(sizeDp));
        star.Size = new Vector2(Tok.Dp(sizeDp), Tok.Dp(sizeDp));
        star.MouseFilter = MouseFilterEnum.Ignore;
        return star;
    }

    public void SetFilled(bool filled)
    {
        _filled = filled;
        QueueRedraw();
    }

    /// <summary>Scale 0 → 1.3 → 1.0 with a BACK/OUT settle.</summary>
    public void Land()
    {
        if (!IsInsideTree()) return;
        PivotOffset = Size * 0.5f;
        Scale = Vector2.Zero;
        Tween tween = UiAnim.On(this, "land");
        tween.TweenProperty(this, "scale", new Vector2(1.3f, 1.3f), UiAnim.Dur(0.11f))
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(this, "scale", Vector2.One, UiAnim.Dur(0.07f))
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    public override void _Draw()
    {
        float d = Mathf.Min(Size.X, Size.Y);
        if (d <= 1f) return;
        Vector2 centre = Size * 0.5f;
        Vector2[] points = IconGlyph.StarPoints(centre, d * 0.46f, d * 0.19f);

        if (_filled)
        {
            DrawColoredPolygon(points, Tok.GoldPale);
            return;
        }

        // Outline only: the point is that the slot is still open.
        var closed = new Vector2[points.Length + 1];
        points.CopyTo(closed, 0);
        closed[^1] = points[0];
        DrawPolyline(closed, Tok.LineHair, Mathf.Max(1f, Tok.Dp(1f)), true);
    }
}
