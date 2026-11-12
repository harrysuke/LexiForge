using Godot;

namespace LexiForge.UI;

/// <summary>
/// The Bonus Jar (UI-30): a vessel, not a number.
///
/// Eight unlabelled segments, 28 dp, filling bottom-up. No text, ever - a number
/// is a promise, a filling vessel is a question, and questions get checked. This
/// is E3's entire visible carrier, so it is the only object on the HUD that
/// accumulates without a label.
///
/// Coins arc INTO it from the cell the player just filled (attribution), the jar
/// jiggles, and when it is full it bursts. A number that increments is not a
/// variable reward.
/// </summary>
public sealed partial class BonusJar : Control
{
    private int _fill;
    private int _cap = 8;
    private float _flash;
    private float _coinT = -1f;
    private Vector2 _coinFrom;
    private float _burstT = -1f;
    private float _jiggle;

    public int Fill => _fill;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(Tok.Dp(28f), Tok.Dp(28f));
        Size = new Vector2(Tok.Dp(28f), Tok.Dp(28f));
        PivotOffset = Size * 0.5f;
        SetProcess(true);
    }

    public void SetFill(int fill, int cap, bool animate)
    {
        _cap = Mathf.Max(1, cap);
        int clamped = Mathf.Clamp(fill, 0, _cap);
        bool gained = clamped > _fill;
        _fill = clamped;
        QueueRedraw();
        if (!gained || !animate || !IsInsideTree()) return;

        // The new segment flashes Gold.Pale for 80 ms, then settles to Gold.
        Tween flash = UiAnim.On(this, "flash");
        flash.TweenMethod(Callable.From<float>(v => { _flash = v; QueueRedraw(); }), 1f, 0f, UiAnim.Dur(0.08f));

        Tween jiggle = UiAnim.On(this, "jiggle");
        jiggle.TweenMethod(Callable.From<float>(v => { _jiggle = v; QueueRedraw(); }), 1f, 0f, UiAnim.Dur(0.22f));
    }

    /// <summary>A coin arcs from a global position into the jar (180 ms, QUINT/OUT).</summary>
    public void FlyCoinFrom(Vector2 globalPosition)
    {
        if (!IsInsideTree()) return;
        _coinFrom = GetGlobalTransformWithCanvas().AffineInverse() * globalPosition;
        Tween tween = UiAnim.On(this, "coin");
        tween.TweenMethod(Callable.From<float>(v => { _coinT = v; QueueRedraw(); }), 0f, 1f, UiAnim.Dur(0.18f))
             .SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(() => { _coinT = -1f; QueueRedraw(); }));
    }

    /// <summary>8/8: eight coins eject radially, the jar scales 1.18 → 0 (420 ms).</summary>
    public void Burst()
    {
        if (!IsInsideTree()) return;
        Tween tween = UiAnim.On(this, "burst");
        tween.TweenMethod(Callable.From<float>(v => { _burstT = v; QueueRedraw(); }), 0f, 1f, UiAnim.Dur(0.42f))
             .SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.Out);
        tween.TweenCallback(Callable.From(() =>
        {
            _burstT = -1f;
            QueueRedraw();
        }));

        PivotOffset = Size * 0.5f;
        Tween scale = UiAnim.On(this, "burstscale");
        scale.TweenProperty(this, "scale", new Vector2(1.18f, 1.18f), UiAnim.Dur(0.14f))
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        scale.TweenProperty(this, "scale", new Vector2(0.0f, 0.0f), UiAnim.Dur(0.28f))
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.In);
    }

    public override void _Draw()
    {
        float d = Mathf.Min(Size.X, Size.Y);
        if (d <= 1f) return;

        float jiggle = _jiggle > 0f ? Mathf.Sin(_jiggle * Mathf.Tau * 2f) * Tok.Dp(4f) * _jiggle : 0f;
        var vessel = new Rect2(jiggle, 0, d, d);

        // 1 dp gold rim, no fill: the jar is a container, not a button.
        Ui.FillRoundRect(this, vessel.Grow(-Tok.Dp(1f)), Tok.Dp(4f), Tok.A(Tok.Surf1, 0.55f));
        Ui.StrokeRoundRect(this, vessel, Tok.Dp(4f), Tok.A(Tok.Gold, 0.85f), 1f);

        // Eight segments, filling bottom-up: physical, not a progress bar.
        // The stack is sized FROM the vessel, never the other way round - a fixed
        // 3 dp segment overflows the rim as soon as the jar shrinks for a narrow HUD.
        float inner = d - Tok.Dp(8f);
        float segGap = Tok.Dp(1f);
        float segH = Mathf.Max(Tok.Dp(1.5f), (inner - (_cap - 1) * segGap) / _cap);
        float stackH = _cap * segH + (_cap - 1) * segGap;
        float x = vessel.Position.X + Tok.Dp(4f);
        float bottom = vessel.Position.Y + (d + stackH) * 0.5f;

        for (int i = 0; i < _cap; i++)
        {
            float y = bottom - (i + 1) * segH - i * segGap;
            var segment = new Rect2(x, y, inner, segH);
            bool filled = i < _fill;
            bool newest = filled && i == _fill - 1 && _flash > 0.01f;
            Color color = newest ? Tok.GoldPale : filled ? Tok.Gold : Tok.A(Tok.LineHair, 0.9f);
            Ui.FillRoundRect(this, segment, 1f, color);
        }

        // Incoming coin: an arc, never a straight slide.
        if (_coinT >= 0f)
        {
            Vector2 to = vessel.GetCenter();
            Vector2 control = (_coinFrom + to) * 0.5f + new Vector2(0, -Tok.Dp(18f));
            float u = 1f - _coinT;
            Vector2 p = u * u * _coinFrom + 2f * u * _coinT * control + _coinT * _coinT * to;
            DrawCircle(p, Mathf.Max(1.5f, Tok.Dp(2.5f)), Tok.Gold);
        }

        // Burst: eight coins, radial, with a gravity arc.
        if (_burstT >= 0f)
        {
            Vector2 centre = vessel.GetCenter();
            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.Tau / 8f;
                Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
                Vector2 p = centre + dir * Tok.Dp(34f) * _burstT
                            + new Vector2(0, Tok.Dp(22f) * _burstT * _burstT);
                DrawCircle(p, Mathf.Max(1.5f, Tok.Dp(2.5f) * (1f - _burstT * 0.6f)), Tok.Gold);
            }
        }
    }
}
