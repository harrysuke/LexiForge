using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// Motion helpers implementing UI_VISUAL_SYSTEM section 8.1 (easing tokens) and
/// UI_DESIGN UI-16/UI-18 (never block input; collapse to a crossfade when the
/// player removes animations).
///
/// Two rules this class exists to enforce:
///  - PivotOffset is set before any Scale tween, so a tile presses instead of
///    sliding from its top-left corner (section 8.4 note 1).
///  - One tween per (node, channel). Re-entering an animation kills the previous
///    one instead of stacking two writers on the same property.
/// </summary>
public static class UiAnim
{
    private static readonly Dictionary<(ulong Node, string Channel), Tween> Live = new();

    /// <summary>UI-18: every fly/arc/pulse collapses to a 100 ms crossfade.</summary>
    public static bool Reduced
    {
        get
        {
            var save = SaveService.Instance;
            return save is not null && save.Data.RemoveAnimations;
        }
    }

    public static float Dur(float seconds) => Reduced ? 0.1f : seconds;

    /// <summary>Starts a tween on a channel, killing whatever was animating it.</summary>
    public static Tween On(Node node, string channel)
    {
        var key = (node.GetInstanceId(), channel);
        if (Live.TryGetValue(key, out Tween? previous) && GodotObject.IsInstanceValid(previous))
            previous.Kill();
        Tween tween = node.CreateTween();
        Live[key] = tween;
        return tween;
    }

    /// <summary>Every button, no exceptions: scale 0.96 on press, release with 2% overshoot (section 6.3).</summary>
    public static void PressFeedback(Control control, bool down)
    {
        if (control.Size == Vector2.Zero) return;
        control.PivotOffset = control.Size * 0.5f;
        Tween tween = On(control, "press");
        if (Reduced)
        {
            tween.TweenProperty(control, "scale", down ? new Vector2(0.98f, 0.98f) : Vector2.One, 0.1f);
            return;
        }
        if (down)
        {
            tween.SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out)
                 .TweenProperty(control, "scale", new Vector2(0.96f, 0.96f), Tok.TPress);
        }
        else
        {
            tween.SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out)
                 .TweenProperty(control, "scale", Vector2.One, 0.14f);
        }
    }

    /// <summary>Scale pulse about the centre. Used for the wallet pop and star landings.</summary>
    public static void Pulse(Control control, float peak, float seconds)
    {
        if (control.Size == Vector2.Zero) return;
        control.PivotOffset = control.Size * 0.5f;
        Tween tween = On(control, "pulse");
        if (Reduced)
        {
            tween.TweenProperty(control, "scale", Vector2.One, 0.1f);
            return;
        }
        float mid = Mathf.Max(0.01f, seconds * 0.45f);
        tween.TweenProperty(control, "scale", new Vector2(peak, peak), mid)
             .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(control, "scale", Vector2.One, seconds - mid)
             .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
    }

    /// <summary>Card / sheet entrance: rise + fade. `fromDp` is a downward offset in dp.</summary>
    public static void RiseIn(CanvasItem item, float fromDp, float seconds)
    {
        if (item is not Control control) return;
        Vector2 target = control.Position;
        control.Position = target + new Vector2(0, Tok.Dp(fromDp));
        control.Modulate = new Color(1, 1, 1, 0);
        Tween tween = On(control, "rise");
        tween.SetParallel(true);
        tween.TweenProperty(control, "position", target, Dur(seconds))
             .SetTrans(Tween.TransitionType.Expo).SetEase(Tween.EaseType.Out);
        tween.TweenProperty(control, "modulate:a", 1.0f, Dur(seconds) * 0.8f);
    }

    public static void FadeTo(CanvasItem item, float alpha, float seconds, Action? done = null)
    {
        Tween tween = On(item, "fade");
        tween.TweenProperty(item, "modulate:a", alpha, Dur(seconds));
        if (done is not null) tween.TweenCallback(Callable.From(done));
    }

    /// <summary>
    /// Rejection only (UI-P2): a 6 dp horizontal wobble with a sine decay. The
    /// node must be free-positioned - never call this on a container child.
    /// </summary>
    public static void Wobble(Control control, float amplitudeDp, float seconds)
    {
        if (Reduced) return;
        Vector2 origin = control.Position;
        float amp = Tok.Dp(amplitudeDp);
        Tween tween = On(control, "wobble");
        tween.TweenMethod(Callable.From<float>(t =>
        {
            float wave = Mathf.Sin(t * Mathf.Pi * 4f) * Mathf.Exp(-4f * t);
            control.Position = origin + new Vector2(amp * wave, 0);
        }), 0f, 1f, seconds);
        tween.TweenCallback(Callable.From(() => control.Position = origin));
    }

    /// <summary>
    /// Arc, not lerp (section 8.4 note 2): quadratic bezier with a perpendicular
    /// control point, so a flying tile reads as thrown rather than conveyed.
    /// </summary>
    public static void Arc(Control control, Vector2 from, Vector2 to, float liftDp, float seconds, Action? done = null)
    {
        control.Position = from;
        if (Reduced)
        {
            control.Position = to;
            done?.Invoke();
            return;
        }
        Vector2 mid = (from + to) * 0.5f;
        Vector2 direction = (to - from).Normalized();
        Vector2 perpendicular = new(-direction.Y, direction.X);
        Vector2 controlPoint = mid + perpendicular * Tok.Dp(liftDp);

        Tween tween = On(control, "arc");
        tween.TweenMethod(Callable.From<float>(t =>
        {
            float u = 1f - t;
            control.Position = u * u * from + 2f * u * t * controlPoint + t * t * to;
        }), 0f, 1f, Dur(seconds));
        if (done is not null) tween.TweenCallback(Callable.From(done));
    }

    /// <summary>Rolling counter (UI-48). A static number is a receipt; a roll is the reward.</summary>
    public static void CountUp(Label label, int from, int to, float seconds, string format, Action? done = null)
    {
        Tween tween = On(label, "count");
        tween.TweenMethod(Callable.From<float>(v => label.Text = string.Format(format, Mathf.RoundToInt(v))),
                          (float)from, (float)to, Dur(seconds));
        if (done is not null) tween.TweenCallback(Callable.From(done));
    }

    /// <summary>UI-19: haptics are a first-class output channel, independently toggleable.</summary>
    public static void Haptic(int milliseconds)
    {
        var save = SaveService.Instance;
        if (save is not null && !save.Data.HapticsEnabled) return;
        if (OS.GetName() is "Android" or "iOS") Input.VibrateHandheld(milliseconds);
    }
}
