using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// One rack tile. The most-touched object in the game, so it carries the most
/// states (UI_VISUAL_SYSTEM section 6.1):
///
///   Rest · Held · Selected · Consumed · Nudge · Wild · DraggedOver
///
/// Shape grammar (section 2.2): round = in your hand. Rack tiles are circles and
/// move; grid slots are squircles and never move. A player learns this in one
/// forge without being taught.
///
/// Drawn with primitives rather than a Button + StyleBox: a circle needs a
/// half-size corner radius, a top-left inner highlight and a rim that differs
/// from the fill, which is three draws instead of three nested styleboxes.
/// </summary>
public sealed partial class RackTile : Control
{
    public enum TileState { Rest, Held, Selected, Consumed, Nudge, Wild, DraggedOver }

    private Label _glyph = null!;
    private char _letter = 'a';
    private TileState _state = TileState.Rest;
    private float _diameterPx;
    private float _glow;
    private int _selectionOrder;

    public int TileIndex { get; set; }
    public char Letter => _letter;
    public TileState State => _state;
    public Vector2 BasePosition { get; private set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        EnsureGlyph();
        ApplyVisual(false);
        QueueRedraw();
    }

    /// <summary>
    /// Builds the letter Label on demand. The rack configures tiles before they
    /// enter the tree (so the first frame never shows an unpositioned tile), so
    /// setup cannot rely on _Ready having run.
    /// </summary>
    private void EnsureGlyph()
    {
        if (_glyph is not null) return;
        _glyph = Ui.MakeLabel("", 34f, Tok.Text);
        _glyph.MouseFilter = MouseFilterEnum.Ignore;
        _glyph.VerticalAlignment = VerticalAlignment.Center;
        _glyph.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_glyph);

        // UI-15: the dyslexia-friendly toggle applies to grid + rack + slots only.
        bool dyslexia = SaveService.Instance is not null && SaveService.Instance.Data.DyslexiaFont;
        _glyph.AddThemeFontOverride("font", FontBook.Tile(this, dyslexia));
    }

    public void SetDiameter(float diameterPx)
    {
        EnsureGlyph();
        _diameterPx = diameterPx;
        CustomMinimumSize = new Vector2(diameterPx, diameterPx);
        Size = new Vector2(diameterPx, diameterPx);
        PivotOffset = Size * 0.5f;

        // The tool is the largest letterform on screen, but it must never
        // overflow a compact 56 dp tile: scale the glyph with the tile.
        float letterSp = 34f * Mathf.Clamp(diameterPx / Tok.Dp(Tok.RackTileDp), 0.72f, 1.15f);
        _glyph.AddThemeFontSizeOverride("font_size", Tok.I(letterSp));
        _glyph.Size = Size;
        _glyph.Position = Vector2.Zero;
        QueueRedraw();
    }

    public void SetLetter(char letter)
    {
        EnsureGlyph();
        _letter = letter;
        bool wild = letter == '*';
        _glyph.Visible = !wild;
        _glyph.Text = wild ? "" : char.ToUpperInvariant(letter).ToString();
        QueueRedraw();
    }

    public void SetState(TileState state, bool animate = true)
    {
        if (_state == state) return;
        _state = state;
        ApplyVisual(animate);
        QueueRedraw();
    }

    public void SetSelectionOrder(int order)
    {
        if (_selectionOrder == order) return;
        _selectionOrder = order;
        QueueRedraw();
    }

    /// <summary>0..1 breathing value driven by the rack's single nudge loop (UI-36).</summary>
    public void SetGlow(float glow)
    {
        if (Mathf.Abs(_glow - glow) < 0.01f) return;
        _glow = glow;
        QueueRedraw();
    }

    /// <summary>Moves the tile to a new slot in the rack. `delay` staggers a re-deal.</summary>
    public void Place(Vector2 basePosition, bool animate, float delay = 0f)
    {
        BasePosition = basePosition;
        Vector2 target = basePosition + new Vector2(0, Tok.Dp(LiftDp()));
        if (!animate || !IsInsideTree())
        {
            Position = target;
            return;
        }
        Tween tween = UiAnim.On(this, "move");
        if (delay > 0f) tween.TweenInterval(UiAnim.Dur(delay));
        tween.TweenProperty(this, "position", target, UiAnim.Dur(0.18f))
             .SetTrans(Tween.TransitionType.Quint).SetEase(Tween.EaseType.Out);
    }

    private float LiftDp() => _state switch
    {
        TileState.Held or TileState.Selected or TileState.DraggedOver => -6f,
        _ => 0f
    };

    private float ScaleForState() => _state switch
    {
        TileState.Held => 1.08f,
        TileState.Selected => 1.04f,
        TileState.DraggedOver => 1.10f,
        TileState.Consumed => 0.94f,
        _ => 1.0f
    };

    private void ApplyVisual(bool animate)
    {
        EnsureGlyph();
        _glyph.AddThemeColorOverride("font_color", _state switch
        {
            TileState.Selected or TileState.Held => Tok.Gold,
            TileState.Consumed => Tok.TextDim,
            TileState.Wild => Tok.GoldPale,
            TileState.Nudge => Tok.Ember,
            _ => Tok.Text
        });

        float scale = ScaleForState();
        if (!animate || !IsInsideTree())
        {
            Scale = new Vector2(scale, scale);
        }
        else
        {
            UiAnim.On(this, "scale")
                  .TweenProperty(this, "scale", new Vector2(scale, scale), UiAnim.Dur(0.12f))
                  .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        Place(BasePosition, animate);
    }

    public override void _Draw()
    {
        float d = _diameterPx > 0 ? _diameterPx : Size.X;
        if (d <= 1) return;
        Vector2 centre = new(d * 0.5f, d * 0.5f);
        float radius = d * 0.5f - Mathf.Max(1f, Tok.Dp(1f));

        // 2 dp drop shadow, y+2: every tile sits on the bench, not in it.
        DrawCircle(centre + new Vector2(0, Tok.Dp(2f)), radius, new Color(0, 0, 0, 0.35f));

        // Bloom behind held / selected / nudge tiles (Ember @ 18%).
        if (_state is TileState.Held or TileState.Selected or TileState.DraggedOver || _glow > 0.01f)
        {
            float glowStrength = _state is TileState.Held or TileState.Selected or TileState.DraggedOver
                ? 0.18f
                : 0.10f + 0.14f * _glow;
            Color tint = _state == TileState.Nudge || _glow > 0.01f ? Tok.Ember : Tok.Gold;
            for (int i = 3; i >= 1; i--)
                DrawCircle(centre, radius + Tok.Dp(2f * i), Tok.A(tint, glowStrength / (i + 1)));
        }

        Color fill = _state switch
        {
            TileState.Held or TileState.Selected or TileState.DraggedOver => Tok.Surf3,
            _ => Tok.Surf2
        };
        DrawCircle(centre, radius, fill);

        // Consumed: warm dim, never grey (C-3). Grey on a warm ground reads as
        // "broken" and raises perceived contrast instead of lowering it.
        if (_state == TileState.Consumed)
            DrawCircle(centre, radius, Tok.A(Tok.BgBase, 0.55f));

        // 1 dp top-left inner highlight: the tile reads as lit from above.
        DrawArc(centre, radius - Tok.Dp(1f), Mathf.Pi * 1.05f, Mathf.Pi * 1.75f, 14,
                new Color(1, 1, 1, 0.06f), Mathf.Max(1f, Tok.Dp(1f)), true);

        // Rim.
        Color rim = _state switch
        {
            TileState.Nudge => Tok.Ember,
            TileState.DraggedOver => Tok.GoldPale,
            TileState.Held or TileState.Selected => Tok.Gold,
            TileState.Wild => Tok.Gold,
            _ => new Color(0, 0, 0, 0)
        };
        float rimWidth = _state switch
        {
            TileState.Nudge => Tok.Dp(3f),
            TileState.Wild => Tok.Dp(1f),
            TileState.Rest or TileState.Consumed => 0f,
            _ => Tok.Dp(2f)
        };
        if (rimWidth > 0f)
            DrawArc(centre, radius - rimWidth * 0.5f, 0, Mathf.Tau, 40, rim, rimWidth, true);

        // Wild tile: a star instead of a letter, so it is never mistaken for one.
        if (_letter == '*')
            DrawColoredPolygon(IconGlyph.StarPoints(centre, radius * 0.52f, radius * 0.22f), Tok.GoldPale);

        // Selection-order pip: tells the player where this tile sits in the word.
        if (_selectionOrder > 0)
        {
            Vector2 pip = centre + new Vector2(radius * 0.55f, -radius * 0.55f);
            DrawCircle(pip, Mathf.Max(1.5f, Tok.Dp(3f)), Tok.GoldPale);
        }
    }
}
