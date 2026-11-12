using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// The rewarded-ad row (UI_VISUAL_SYSTEM section 6.9) - the core ad component.
///
/// Rules this component encodes, all of them about arithmetic and restraint:
///  - **Show both numbers** (`148 → 296`). A delta is a fact; an adjective is a
///    claim.
///  - **Never gold as a container.** Gold means *earned*. The row stays Surf.1 so
///    the reward inside it reads brighter.
///  - **The AD badge is always visible** before the tap (UI-20).
///  - **Loading is a 2 dp sweep, never a spinner or an overlay**: input stays live
///    and the screen is never dimmed.
///  - **No pulsing, no red dots, no bounce loops.** A pulsing ad button is
///    coercion in pixels.
/// </summary>
public sealed partial class RewardedRow : Button
{
    /// <summary>
    /// The one string every ad row uses for its label. "Watch a sponsor's message"
    /// needs ~875 px at 20 sp and the row only has ~780 on the 360 dp column, so it
    /// ellipsised on the device that matters. Shorter copy, same honesty: the AD
    /// badge and the exact reward are both still visible before the tap (UI-20).
    /// </summary>
    public const string AdCopy = "Watch a sponsor ad";

    private MarginContainer _frame = null!;
    private IconGlyph _icon = null!;
    private Label _value = null!;
    private Label _delta = null!;
    private AdBadge _badge = null!;
    private ColorRect _sweep = null!;
    private Action? _onPressed;
    private string _pendingValue = "";
    private Glyph _pendingIcon = Glyph.Mark;
    private string _pendingDelta = "";
    private bool _built;
    private bool _loading;

    public static RewardedRow Create(string value, string delta, Action onPressed,
                                     float heightDp = 72f, Glyph icon = Glyph.Mark)
    {
        // The labels do not exist until _Ready, so the text is parked here and
        // applied by Build(). Dropping it made every caller that used Create()
        // without a follow-up Configure() render an empty row.
        //
        // `icon` is parked for the same reason. It defaults to Mark because that is
        // what most rewarded rows grant, but a row that pays out something else must
        // say so: a diamond next to "+1 reveal" reads as "you get a Mark", which is
        // the wrong promise.
        var row = new RewardedRow
        {
            _onPressed = onPressed, _pendingValue = value, _pendingDelta = delta, _pendingIcon = icon
        };
        row.CustomMinimumSize = new Vector2(0, Tok.Dp(heightDp));
        row.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return row;
    }

    public override void _Ready()
    {
        FocusMode = FocusModeEnum.None;
        ClipText = true;
        ClipContents = true;
        Ui.Style(this, BtnKind.Secondary);
        Build();

        ButtonDown += () =>
        {
            UiAnim.PressFeedback(this, true);
            UiAnim.Haptic(12);
        };
        ButtonUp += () => UiAnim.PressFeedback(this, false);
        Pressed += () =>
        {
            // Hold the pressed state so the tap is acknowledged rather than
            // swallowed by the SDK taking the screen.
            EventBus.Cue("ui.tap");
            _onPressed?.Invoke();
        };
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        var frame = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        _frame = frame;
        frame.AddThemeConstantOverride("margin_left", Tok.I(16f));
        frame.AddThemeConstantOverride("margin_right", Tok.I(12f));
        frame.AddThemeConstantOverride("margin_top", Tok.I(8f));
        frame.AddThemeConstantOverride("margin_bottom", Tok.I(8f));
        AddChild(frame);
        Ui.FullRect(frame);

        var row = new HBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        row.AddThemeConstantOverride("separation", Tok.I(12f));
        frame.AddChild(row);

        _icon = IconGlyph.Create(_pendingIcon, 20f, Tok.GoldPale, 1.8f);
        _icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        _icon.Visible = _pendingIcon != Glyph.None;
        row.AddChild(_icon);

        var lines = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ShrinkCenter
        };
        lines.AddThemeConstantOverride("separation", Tok.I(1f));
        row.AddChild(lines);

        // Both labels trim instead of pushing: a Label's minimum width is its full
        // text width, which shoved the AD badge out of the row and clipped the copy
        // ("Watch a sponsor's messa...") on the 360 dp column.
        _value = Ui.MakeLabel("", 20f, Tok.Text, HorizontalAlignment.Left);
        _value.MouseFilter = MouseFilterEnum.Ignore;
        _value.ClipText = true;
        _value.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lines.AddChild(_value);

        _delta = Ui.MakeLabel("", 20f, Tok.Gold, HorizontalAlignment.Left);
        _delta.MouseFilter = MouseFilterEnum.Ignore;
        _delta.ClipText = true;
        _delta.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        lines.AddChild(_delta);

        _badge = AdBadge.Create();
        _badge.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(_badge);

        _value.Text = _pendingValue;
        _delta.Text = _pendingDelta;
        _delta.Visible = !string.IsNullOrEmpty(_pendingDelta);

        FitHeight();

        // 2 dp progress sweep along the bottom edge: no spinner, no overlay.
        _sweep = new ColorRect
        {
            Color = Tok.A(Tok.Gold, 0.9f),
            MouseFilter = MouseFilterEnum.Ignore,
            Visible = false,
            Size = new Vector2(Tok.Dp(64f), Tok.Dp(2f))
        };
        AddChild(_sweep);
    }

    public void Configure(string value, string delta, Action onPressed, Glyph icon = Glyph.Mark)
    {
        Build();
        _onPressed = onPressed;
        _value.Text = value;
        _delta.Text = delta;
        _delta.Visible = !string.IsNullOrEmpty(delta);
        _icon.SetGlyph(icon);
        // Glyph.None frees the 20 dp slot for the copy. The AD badge already says
        // what the row is, so the leading mark is optional, not load-bearing.
        _icon.Visible = icon != Glyph.None;
        FitHeight();
    }

    /// <summary>
    /// A Button is not a Container: its children never contribute to its own
    /// minimum size, so a two-line row silently overflows the 64/72 dp floor the
    /// caller asked for - and then spills past the card that sized itself from it.
    ///
    /// The height is derived from the LABELS, not from the frame's combined minimum:
    /// a container's minimum is only valid after the next sort, so asking it here
    /// returned the one-line height and the second line was still cut.
    /// </summary>
    private void FitHeight()
    {
        if (_value is null) return;
        float lines = _value.GetCombinedMinimumSize().Y;
        if (_delta.Visible) lines += _delta.GetCombinedMinimumSize().Y + Tok.Dp(1f);
        float needed = lines + Tok.Dp(16f);
        if (needed > CustomMinimumSize.Y)
            CustomMinimumSize = new Vector2(CustomMinimumSize.X, needed);
    }

    public void SetLoading(bool loading)
    {
        _loading = loading;
        _sweep.Visible = loading;
        Disabled = loading;
        if (!loading)
        {
            UiAnim.On(_sweep, "sweep").Kill();
            return;
        }
        if (Size.X < 1f) return;
        _sweep.Position = new Vector2(0, Size.Y - Tok.Dp(2f));
        Tween tween = UiAnim.On(_sweep, "sweep");
        tween.SetLoops();
        tween.TweenProperty(_sweep, "position:x", Size.X, 0.9f)
             .From(0f)
             .SetTrans(Tween.TransitionType.Linear);
    }

    /// <summary>Reward granted: the badge flips to a jade check for 1.2 s.</summary>
    public void FlashGranted()
    {
        if (_badge is null) return;
        _badge.SetGranted(true);
        Tween tween = UiAnim.On(_badge, "granted");
        tween.TweenInterval(1.2);
        tween.TweenCallback(Callable.From(() => _badge.SetGranted(false)));
    }

    public bool IsLoading => _loading;

    public override void _Notification(int what)
    {
        if (what != NotificationResized || _sweep is null) return;
        _sweep.Size = new Vector2(Tok.Dp(64f), Tok.Dp(2f));
        _sweep.Position = new Vector2(0, Size.Y - Tok.Dp(2f));
    }
}
