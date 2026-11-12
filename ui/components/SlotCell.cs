using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// One grid cell. Six states from UI_VISUAL_SYSTEM section 6.2:
///
///   Empty · Empty+pips · Keystone · Filled · Locking · Keystone lock
///
/// The structural distinction that makes E2 work: **empty slots are carved,
/// filled slots are stamped.**
///  - carved  = dark fill, 3.31:1 rim, inset shadow along the top inner edge
///  - stamped = gradient fill, 1 dp top highlight, drop shadow, 2% larger
///
/// The measured delta between the two fills is only 1.14:1 - deliberately
/// quiet, because the LIGHTING does the work, not the fill. That is how UI-08
/// ("not a solid block") and UI-09 ("filled is brighter than empty") are both
/// true at once.
/// </summary>
public sealed partial class SlotCell : Control
{
    private float _sizePx;
    private bool _keystone;
    private bool _showPips;
    private bool _showCount;
    private bool _vertical;
    private int _letterCount;
    private char _letter;
    private bool _filled;
    private bool _locked;
    private float _bloom;

    private Font? _font;
    private bool _fontResolved;

    public void Configure(float sizePx, bool keystone, bool showPips, bool showCount,
                          int letterCount, bool vertical)
    {
        _sizePx = sizePx;
        _keystone = keystone;
        _showPips = showPips;
        _showCount = showCount;
        _letterCount = letterCount;
        _vertical = vertical;
        CustomMinimumSize = new Vector2(sizePx, sizePx);
        Size = new Vector2(sizePx, sizePx);
        PivotOffset = Size * 0.5f;
        QueueRedraw();
    }

    public void SetContent(char letter, bool filled, bool locked)
    {
        bool changed = _filled != filled || _letter != letter || _locked != locked;
        _letter = letter;
        _filled = filled;
        _locked = locked;
        if (changed) QueueRedraw();
    }

    /// <summary>120 ms snap: scale 1.06 → 1.0 with a one-frame gold bloom (section 8.2).</summary>
    public void PlayLock(bool keystone)
    {
        _bloom = 1f;
        QueueRedraw();
        Tween bloom = UiAnim.On(this, "bloom");
        bloom.TweenMethod(Callable.From<float>(v => { _bloom = v; QueueRedraw(); }), 1f, 0f, UiAnim.Dur(0.18f));

        if (!IsInsideTree()) return;
        Scale = new Vector2(1.06f, 1.06f);
        UiAnim.On(this, "lock")
              .TweenProperty(this, "scale", Vector2.One, UiAnim.Dur(Tok.TLock))
              .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
    }

    private Font ResolveFont()
    {
        if (_fontResolved) return _font!;
        bool dyslexia = SaveService.Instance is not null && SaveService.Instance.Data.DyslexiaFont;
        _font = FontBook.Tile(this, dyslexia);
        _fontResolved = true;
        return _font;
    }

    public override void _Draw()
    {
        float d = _sizePx > 0 ? _sizePx : Size.X;
        if (d <= 1f) return;

        float inset = Mathf.Max(1f, Tok.Dp(1f));
        var rect = new Rect2(inset, inset, d - inset * 2f, d - inset * 2f);
        float radius = _keystone ? Tok.RadiusKeystoneDp : Tok.RadiusSlotDp;

        if (_filled)
        {
            // Stamped: proud of the surface.
            var shadow = new StyleBoxFlat { BgColor = new Color(0, 0, 0, 0.4f), ShadowSize = Tok.I(2f) };
            shadow.SetCornerRadiusAll(Tok.I(radius));
            shadow.ShadowOffset = new Vector2(0, Tok.Dp(2f));
            DrawStyleBox(shadow, rect);

            Ui.FillRoundRect(this, rect, radius, Tok.Surf3);
            // 1 dp top highlight + 1 dp bottom shade = a bevel, not a flat tile.
            Ui.StrokeRoundRect(this, rect, radius, new Color(1, 1, 1, 0.12f), 1f);
            DrawLine(new Vector2(rect.Position.X + Tok.Dp(4f), rect.End.Y - inset),
                     new Vector2(rect.End.X - Tok.Dp(4f), rect.End.Y - inset), Tok.A(Tok.BgVoid, 0.5f), Tok.Dp(1f));
        }
        else
        {
            // Carved: a mold in the plate. The rim carries the shape, not the fill.
            Ui.FillRoundRect(this, rect, radius, Tok.SurfSlot);
            Ui.StrokeRoundRect(this, rect, radius, Tok.LineStrong, _keystone ? 2f : 2f);
            // Inset shadow: a dark band along the top inner edge only.
            var insetShadow = new StyleBoxFlat
            {
                BgColor = new Color(0, 0, 0, 0),
                BorderColor = new Color(0, 0, 0, 0.4f),
                BorderWidthTop = Tok.I(3f)
            };
            insetShadow.SetCornerRadiusAll(Tok.I(radius));
            DrawStyleBox(insetShadow, rect);

            if (_keystone)
            {
                // UI-27 / section 2.2: the only slot with a different silhouette.
                // A notch survives greyscale, so the hard slot is identifiable
                // without colour.
                var notch = new Rect2(d * 0.5f - Tok.Dp(2f), -inset, Tok.Dp(4f), Tok.Dp(6f));
                DrawRect(notch, Tok.BgBase);
                Ui.StrokeRoundRect(this, rect.Grow(-Tok.Dp(1f)), radius - 1f, Tok.A(Tok.GoldPale, 0.6f), 1f);
            }
        }

        // Gold bloom on the lock frame: the "click" of the whole game.
        if (_bloom > 0.01f)
            Ui.FillRoundRect(this, rect, radius, Tok.A(Tok.GoldPale, 0.30f * _bloom));

        float letterSp = _keystone ? 32f : 30f;
        var centreRect = new Rect2(0, 0, d, d);
        Font font = ResolveFont();

        if (_locked)
        {
            Ui.DrawTextCentred(this, font, "?", centreRect, letterSp, Tok.Muted);
            return;
        }

        if (_filled && _letter != '\0')
        {
            Ui.DrawTextCentred(this, font, char.ToUpperInvariant(_letter).ToString(), centreRect,
                              letterSp, Tok.Text);
            return;
        }

        // UI-28: an unknown-length slot is a dead end; a known-length slot is a
        // riddle. Pips run along the slot's axis so they encode length AND
        // direction without a single word of copy.
        if (_showCount && _letterCount > 0)
        {
            Ui.DrawTextCentred(this, font, _letterCount.ToString(),
                              new Rect2(0, 0, d, d), 20f, Tok.A(Tok.TextDim, 0.75f));
            return;
        }

        if (_showPips && _letterCount > 0)
        {
            float dot = Mathf.Max(1.5f, Tok.Dp(1.5f));
            float step = Tok.Dp(5f);
            float span = (_letterCount - 1) * step;
            Vector2 origin = _vertical
                ? new Vector2(d * 0.5f, d * 0.72f - span * 0.5f)
                : new Vector2(d * 0.5f - span * 0.5f, d * 0.72f);
            for (int i = 0; i < _letterCount; i++)
            {
                Vector2 p = _vertical ? origin + new Vector2(0, i * step) : origin + new Vector2(i * step, 0);
                DrawCircle(p, dot, Tok.A(Tok.Muted, 0.4f));
            }
        }
    }
}
