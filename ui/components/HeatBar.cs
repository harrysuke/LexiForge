using Godot;

namespace LexiForge.UI;

/// <summary>
/// Heat as a RUN METER, never a clock (UI-32, UI-38, UI-P1).
///
///  - 10 dp, full-bleed, at the very top of the screen.
///  - No numeric label at rest. Tapping the bar reveals the number for 1.4 s.
///  - Gradient iron → ember → gold; it must never pass through red (UI-12).
///  - At 80+ the run's score sits INSIDE the right cap, so the thing at risk is
///    named at the moment it becomes at risk (E5).
///  - Upward changes snap in 120 ms (information); downward changes animate over
///    400 ms (the cool is the reward).
/// </summary>
public sealed partial class HeatBar : Control
{
    private double _display;
    private double _target;
    private int _score;
    private Label _readout = null!;
    private double _readoutRemaining;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        CustomMinimumSize = new Vector2(0, Tok.Dp(10f));
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        TooltipText = "";

        _readout = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Center);
        _readout.MouseFilter = MouseFilterEnum.Ignore;
        _readout.Modulate = new Color(1, 1, 1, 0);
        AddChild(_readout);
        LayoutReadout();
        SetProcess(true);
    }

    private void LayoutReadout()
    {
        if (_readout is null) return;
        _readout.Size = new Vector2(Size.X, Tok.Dp(20f));
        _readout.Position = new Vector2(0, Size.Y + Tok.Dp(2f));
    }

    public void SetHeat(double heat, int score, bool animate)
    {
        _target = Mathf.Clamp(heat, 0, 100);
        _score = score;
        if (!animate || !IsInsideTree())
        {
            _display = _target;
            QueueRedraw();
            return;
        }
        double duration = _target < _display ? 0.4 : 0.12;
        Tween tween = UiAnim.On(this, "heat");
        tween.TweenMethod(Callable.From<float>(v => { _display = v; QueueRedraw(); }),
                          (float)_display, (float)_target, UiAnim.Dur((float)duration))
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    public override void _GuiInput(InputEvent @event)
    {
        bool tapped = @event is InputEventScreenTouch { Pressed: true } ||
                      @event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left };
        if (!tapped) return;
        _readout.Text = $"HEAT {_display:0} / 100";
        _readout.Modulate = new Color(1, 1, 1, 1);
        _readoutRemaining = 1.4;
        AcceptEvent();
    }

    public override void _Process(double delta)
    {
        if (_readoutRemaining <= 0) return;
        _readoutRemaining -= delta;
        float alpha = _readoutRemaining > 0.4 ? 1f : Mathf.Max(0f, (float)_readoutRemaining / 0.4f);
        _readout.Modulate = new Color(1, 1, 1, alpha);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationResized) LayoutReadout();
    }

    public override void _Draw()
    {
        float w = Size.X;
        float h = Size.Y;
        if (w <= 1f || h <= 1f) return;

        // Track. Quiet: the empty part of the meter is not information.
        DrawRect(new Rect2(0, 0, w, h), Tok.LineHair);

        float t = (float)(_display / 100.0);
        float fillW = Mathf.Max(0f, w * t);

        Color fill = HeatColour(t);

        // Bloom grows with Heat: 0 → 6 dp at 40 → 12 dp at 80. Gold at the top,
        // which reads as "precious and nearly full", not "danger".
        if (t >= 0.4f)
        {
            float glowDp = t >= 0.8f ? 12f : 6f;
            float strength = t >= 0.8f ? 0.22f : 0.12f;
            Color tint = t >= 0.8f ? Tok.Gold : Tok.Ember;
            for (int i = 3; i >= 1; i--)
                DrawRect(new Rect2(0, h, fillW, Tok.Dp(glowDp) * i / 3f), Tok.A(tint, strength / (i + 1)));
        }

        DrawRect(new Rect2(0, 0, fillW, h), fill);

        // At 80+ the score moves inside the right cap (E5: name the thing at risk).
        if (t >= 0.8f && _score > 0)
        {
            Font font = GetThemeDefaultFont();
            int size = Tok.I(20f);
            string text = _score.ToString("N0");
            Vector2 measured = font.GetStringSize(text, HorizontalAlignment.Left, -1, size);
            if (measured.X + Tok.Dp(12f) < w)
            {
                var rect = new Rect2(w - measured.X - Tok.Dp(10f), 0, measured.X, h);
                Ui.DrawTextCentred(this, font, text, rect, 20f, Tok.Ink);
            }
        }
    }

    private static Color HeatColour(float t)
    {
        if (t < 0.4f) return Tok.HeatIron.Lerp(Tok.HeatWarm, t / 0.4f);
        if (t < 0.8f) return Tok.HeatWarm.Lerp(Tok.Ember, (t - 0.4f) / 0.4f);
        return Tok.Ember.Lerp(Tok.Gold, (t - 0.8f) / 0.2f);
    }
}
