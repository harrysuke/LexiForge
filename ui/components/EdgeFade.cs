using Godot;

namespace LexiForge.UI;

/// <summary>
/// A soft gradient from transparent to the screen background, laid over the edge
/// of a scrolling region.
///
/// Why this exists: the draft cannot show three 180 dp cards on a 640 dp screen,
/// so it scrolls - and a hard clip slices straight through a card's delta line,
/// which reads as a rendering bug rather than as "there is more below". A fade is
/// the standard, wordless scroll affordance: the content dissolves into the
/// background instead of being cut.
///
/// It is drawn as bands rather than a GradientTexture2D so it costs no asset, no
/// texture upload and no import step - and it always matches the colour passed in,
/// so it keeps working if the palette moves.
/// </summary>
public sealed partial class EdgeFade : Control
{
    public enum Edge { Top, Bottom }

    private Edge _edge = Edge.Bottom;
    private Color _color = Colors.Black;

    /// <summary>Bands trade a little smoothness for no texture. 18 is invisible at 56 dp.</summary>
    private const int Bands = 18;

    public static EdgeFade Create(Edge edge, float heightDp, Color color)
    {
        var fade = new EdgeFade
        {
            _edge = edge,
            _color = color,
            MouseFilter = MouseFilterEnum.Ignore
        };
        // Taps belong to the cards underneath: this is decoration, not a surface.
        Ui.AnchorBottom(fade, heightDp);
        return fade;
    }

    public override void _Draw()
    {
        float height = Size.Y;
        if (height <= 0f) return;

        float band = height / Bands;
        for (int i = 0; i < Bands; i++)
        {
            float t = (i + 1) / (float)Bands;
            // Squared ramp: the fade starts almost invisible, so the first card
            // still reads as fully present, and only bites near the clip.
            float alpha = _edge == Edge.Bottom ? t * t : (1f - t) * (1f - t);
            if (alpha <= 0.001f) continue;

            float y = _edge == Edge.Bottom ? i * band : height - (i + 1) * band;
            // +1 px overlap: without it the band seams show as faint lines on
            // some GPUs where the rasteriser snaps each rect to a pixel boundary.
            DrawRect(new Rect2(0, y, Size.X, band + 1f),
                     new Color(_color.R, _color.G, _color.B, _color.A * alpha));
        }
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) QueueRedraw();
    }
}
