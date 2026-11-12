using System;
using Godot;
using LexiForge.Autoload;

namespace LexiForge.UI;

/// <summary>
/// Hint confirm sheet (UI-35). The HUD button shows the remaining COUNT; the PRICE
/// is only shown here, on tap. A visible price on the button turns a help
/// affordance into a shop.
///
/// The rewarded option lives inside this sheet, never on the main HUD (section
/// 9.1: placement R_HINT). It is labelled with the exact reward before playback
/// and sits at the same visual weight as the coin row.
/// </summary>
public sealed partial class HintSheet : Control
{
    [Signal] public delegate void CoinRequestedEventHandler();
    [Signal] public delegate void AdRequestedEventHandler();

    private ColorRect _dim = null!;
    private PanelContainer _sheet = null!;
    private Label _subtitle = null!;
    private Button _coinRow = null!;
    private RewardedRow _adRow = null!;
    private Label _title = null!;

    public bool IsOpen => Visible;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Visible = false;

        _dim = new ColorRect { Color = Tok.A(Tok.BgVoid, 0.55f) };
        AddChild(_dim);
        Ui.FullRect(_dim);

        _sheet = new PanelContainer();
        var style = new StyleBoxFlat { BgColor = Tok.Surf1 };
        style.CornerRadiusTopLeft = Tok.I(24f);
        style.CornerRadiusTopRight = Tok.I(24f);
        style.ContentMarginLeft = Tok.I(Tok.MarginDp);
        style.ContentMarginRight = Tok.I(Tok.MarginDp);
        style.ContentMarginTop = Tok.I(20f);
        style.ContentMarginBottom = Tok.I(20f);
        _sheet.AddThemeStyleboxOverride("panel", style);
        AddChild(_sheet);

        var column = new VBoxContainer();
        column.AddThemeConstantOverride("separation", Tok.I(12f));
        _sheet.AddChild(column);

        _title = Ui.MakeLabel("Reveal a letter", 24f, Tok.Text, HorizontalAlignment.Left);
        column.AddChild(_title);

        _subtitle = Ui.MakeLabel("", 20f, Tok.Muted, HorizontalAlignment.Left, wrap: true);
        column.AddChild(_subtitle);

        _coinRow = Ui.MakeButton("", () => EmitSignal(SignalName.CoinRequested), BtnKind.Secondary, 56f, 22f);
        column.AddChild(_coinRow);

        _adRow = RewardedRow.Create(RewardedRow.AdCopy, "+1 reveal",
                                     () => EmitSignal(SignalName.AdRequested), 72f, Glyph.Bulb);
        column.AddChild(_adRow);

        column.AddChild(Ui.MakeButton("Not now", Close, BtnKind.Ghost, 44f, 20f, "ui.close"));

        Resized += LayoutSheet;
    }

    private void OpenDeferred()
    {
        LayoutSheet();
        UiAnim.RiseIn(_sheet, 40f, 0.26f);
    }

    private void LayoutSheet()
    {
        if (_sheet is null) return;
        float width = Mathf.Min(Size.X, Tok.Dp(Tok.DesignWidthDp));

        // The sheet is as tall as its content, capped so its top edge always stays
        // on screen. The fixed 288 dp it used to have was 32 dp SHORT of the real
        // content, which pushed the "Not now" row below the bottom of the display.
        float content = _sheet.GetCombinedMinimumSize().Y;
        float ceiling = Mathf.Max(Tok.Dp(220f), Size.Y - Tok.Dp(96f));
        float height = Mathf.Clamp(content, Tok.Dp(220f), ceiling);

        _sheet.Size = new Vector2(width, height);
        _sheet.Position = new Vector2((Size.X - width) * 0.5f, Size.Y - height);
    }

    public void Open(int cost, int freeHints, int hintsLeft, bool adAvailable)
    {
        Visible = true;
        _subtitle.Text = hintsLeft > 0
            ? $"{hintsLeft} reveal{(hintsLeft == 1 ? "" : "s")} left in this forge. The next letter of any open word."
            : "No reveals left in this forge.";

        bool free = freeHints > 0;
        _coinRow.Text = free ? "Reveal  ·  free" : $"Reveal  ·  {cost} coins";
        _coinRow.Disabled = !free && hintsLeft <= 0;
        _adRow.Visible = adAvailable && hintsLeft > 0;
        _adRow.SetLoading(false);

        EventBus.Cue("ui.open");
        // The ad row's visibility changed this frame, so the sheet's real height is
        // only known after the container re-sorts.
        CallDeferred(nameof(OpenDeferred));
    }

    public void Close()
    {
        if (!Visible) return;
        Visible = false;
        EventBus.Cue("ui.close");
    }

    public void SetAdLoading(bool loading) => _adRow.SetLoading(loading);
    public void FlashAdGranted() => _adRow.FlashGranted();
}
