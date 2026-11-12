using Godot;

namespace LexiForge.UI;

/// <summary>
/// Flow must be readable WITHOUT reading (UI-31): a multiplier plus a five-segment
/// bar. On a clean find one segment fills and the number rolls; on a break the bar
/// DRAINS over 300 ms and the number holds until the drain finishes. That hold is
/// what makes the loss felt - a number counting down would turn it into a clock.
///
/// Everything is drawn rather than composed from Labels. Labels clamp to their
/// font's line box, and a 30 sp numeral in a 36 dp row would push the caption out
/// of the way - the two would overlap on every device. Drawing lets the glyphs sit
/// on a shared baseline while the row keeps its ladder height.
/// </summary>
public sealed partial class FlowMeter : Control
{
    private const int Segments = 5;
    private const float CaptionSp = 18f;
    private const float ValueSp = 30f;

    private string _valueText = "×1.00";
    private double _display = 1.0;
    private float _barValue;
    private bool _maxed;

    /// <summary>Five segments span 1.00x → 2.50x (the Flow cap in balance_config).</summary>
    private static float BarFor(double flow)
        => Mathf.Clamp((float)((flow - 1.0) / 1.5) * Segments, 0f, Segments);

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, Tok.Dp(36f));
    }

    public void SetFlow(double flow, bool animate)
    {
        double previous = _display;
        float barTarget = BarFor(flow);
        _display = flow;
        _maxed = flow >= 2.495;

        if (!animate || !IsInsideTree() || UiAnim.Reduced)
        {
            _barValue = barTarget;
            _valueText = Format(flow);
            QueueRedraw();
            return;
        }

        if (flow >= previous)
        {
            // Up: number rolls 160 ms, segment fills 40 ms behind it.
            Tween tween = UiAnim.On(this, "flow");
            tween.SetParallel(true);
            tween.TweenMethod(Callable.From<float>(v =>
                             {
                                 _display = v;
                                 _valueText = Format(v);
                                 QueueRedraw();
                             }), (float)previous, (float)flow, 0.16f)
                 .SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out);
            tween.TweenMethod(Callable.From<float>(v => { _barValue = v; QueueRedraw(); }),
                              _barValue, barTarget, 0.20f)
                 .SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out);
            return;
        }

        // Break: segments fade right→left over 300 ms; the number drops at t=300.
        Tween drain = UiAnim.On(this, "flow");
        drain.TweenMethod(Callable.From<float>(v => { _barValue = v; QueueRedraw(); }),
                          _barValue, barTarget, 0.30f)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        drain.TweenCallback(Callable.From(() =>
        {
            _display = flow;
            _valueText = Format(flow);
            QueueRedraw();
        }));
    }

    private static string Format(double flow) => $"×{flow:0.00}";

    public override void _Draw()
    {
        Font font = GetThemeDefaultFont();
        float h = Size.Y;
        if (h < 1f) return;

        int captionSize = Tok.I(CaptionSp);
        int valueSize = Tok.I(ValueSp);

        Vector2 captionBox = font.GetStringSize("FLOW", HorizontalAlignment.Left, -1, captionSize);
        Vector2 valueBox = font.GetStringSize(_valueText, HorizontalAlignment.Left, -1, valueSize);

        float x = 0f;
        DrawLineOfText(font, "FLOW", captionSize, Tok.Muted, x, h, captionBox);
        x += captionBox.X + Tok.Dp(8f);

        DrawLineOfText(font, _valueText, valueSize, Tok.Gold, x, h, valueBox);
        x += valueBox.X + Tok.Dp(10f);

        float segW = Tok.Dp(6f);
        float gap = Tok.Dp(3f);
        float segH = Tok.Dp(14f);
        float segY = (h - segH) * 0.5f;
        float radius = Tok.Dp(3f);

        for (int i = 0; i < Segments; i++)
        {
            var rect = new Rect2(x + i * (segW + gap), segY, segW, segH);
            Ui.FillRoundRect(this, rect, radius, Tok.LineHair);

            float fill = Mathf.Clamp(_barValue - i, 0f, 1f);
            if (fill > 0.001f)
            {
                var filled = new Rect2(rect.Position, new Vector2(rect.Size.X * fill, rect.Size.Y));
                Ui.FillRoundRect(this, filled, radius, Tok.Gold);
            }
        }

        // A permanent, quiet "you are maxed" marker - not a badge, not a banner.
        if (_maxed)
        {
            float underlineY = h - Tok.Dp(2f);
            DrawLine(new Vector2(0, underlineY), new Vector2(x, underlineY),
                     Tok.A(Tok.GoldPale, 0.9f), Tok.Dp(1f));
        }
    }

    /// <summary>Draws one run of text vertically centred on the row's own height.</summary>
    private void DrawLineOfText(Font font, string text, int size, Color color,
                                float x, float rowHeight, Vector2 box)
    {
        float baseline = (rowHeight - box.Y) * 0.5f + font.GetAscent(size);
        DrawString(font, new Vector2(x, baseline), text, HorizontalAlignment.Left, -1, size, color);
    }
}
