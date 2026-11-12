using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.UI;

/// <summary>
/// TEMPORARY dev harness (not shipped). Renders the real screens into an exact
/// 1080x1920 design viewport, drives them the way a player would (tap letters,
/// submit, open sheets), and writes PNGs so the layout can be judged offline.
/// </summary>
public sealed partial class DevSmoke : Control
{
    private const int DesignW = 1080;
    private const int DesignH = 1920;

    private SubViewport _view = null!;
    private Control _host = null!;
    private int _failures;
    private int _steps;

    public override async void _Ready()
    {
        // DEBUG BUILDS ONLY. This harness quits the tree when it finishes and writes
        // PNGs into res:// - it is scaffolding, not the game. Without this guard a
        // stray scene reference could hijack a shipped build; with it, the node
        // removes itself before touching anything.
        if (!OS.IsDebugBuild())
        {
            QueueFree();
            return;
        }

        _view = new SubViewport
        {
            Size = new Vector2I(DesignW, DesignH),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            TransparentBg = false
        };
        AddChild(_view);

        _host = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _view.AddChild(_host);
        Ui.FullRect(_host);

        try
        {
            if (!await EnsureData()) { GD.PushError("[SMOKE] data load failed"); GetTree().Quit(2); return; }
            await RunAll();
        }
        catch (Exception e)
        {
            _failures++;
            GD.PushError("[SMOKE] aborted: " + e);
        }

        GD.Print($"[SMOKE] done — {_steps} steps, {_failures} failure(s)");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private async Task<bool> EnsureData()
    {
        GameDataService.Instance.StartLoad();
        for (int i = 0; i < 100 && !GameDataService.Instance.IsReady; i++) await Wait(0.1);
        if (!GameDataService.Instance.IsReady) return false;
        GD.Print($"[SMOKE] forges={GameDataService.Instance.Forges.Count}");
        return true;
    }

    private async Task RunAll()
    {
        ForgeData forge = GameDataService.Instance.Forges[0];
        GD.Print($"[SMOKE] forge {forge.ForgeId} theme='{forge.Theme}' words={forge.Slots.Count}");
        RunService.Instance.StartRun(20260101, false);

        // ------------------------------------------------------- phase A: forge screen
        Node? node = null;
        ForgeScreen? screen = null;
        Step("forge screen", () =>
        {
            var packed = GD.Load<PackedScene>("res://ui/screens/Forge.tscn");
            node = packed.Instantiate();
            _host.AddChild(node);
            if (node is not ForgeScreen typed) throw new InvalidOperationException("not ForgeScreen");
            screen = typed;
            typed.ResetFor(forge, 0, RuneEffects.None);
            typed.OnEnter(null);
        });
        await Wait(0.7);
        Dump(node!, 0);
        await Shot("01-forge-empty");
        AuditFits(node!, "ForgeScreen/empty");

        LetterRack? rack = null;
        Step("find rack", () =>
        {
            var found = new List<LetterRack>();
            Collect(node!, found);
            rack = found.FirstOrDefault() ?? throw new InvalidOperationException("no LetterRack");
            var tiles = new List<RackTile>();
            Collect(rack, tiles);
            GD.Print($"[SMOKE] rack letters='{string.Join("", tiles.OrderBy(t => t.TileIndex).Select(t => t.Letter))}'");
        });

        // Spell the two shortest non-keystone words, then the keystone, by tapping
        // real tiles: this is the exact path a tap-to-spell player takes.
        var ordered = forge.Slots.Where(s => !s.IsKeystone)
                                 .OrderBy(s => s.Word.Length)
                                 .Concat(forge.Slots.Where(s => s.IsKeystone))
                                 .ToList();

        int spelled = 0;
        foreach (GridSlot slot in ordered)
        {
            bool ok = false;
            Step($"spell '{slot.Word}'", () =>
            {
                ok = SpellWord(rack!, slot.Word.ToLowerInvariant());
                if (!ok) throw new InvalidOperationException("letters unavailable");
            });
            if (!ok) continue;

            if (spelled == 1) await Shot("02-compose");
            Step($"submit '{slot.Word}'", () => rack!.SubmitCurrent());
            spelled++;
            await Wait(spelled == 1 ? 0.9 : 0.45);
            if (spelled == 1)
            {
                await Shot("03-first-word");
                AuditFits(node!, "ForgeScreen/spelling");
            }
        }
        GD.Print($"[SMOKE] spelled {spelled}/{ordered.Count} words");

        await Wait(0.4);
        await Shot("04-results-stars");
        AuditFits(node!, "ForgeScreen/results");
        await Wait(2.6);
        await Shot("05-results-settled");

        // ------------------------------------------------------- phase B: overlays
        ResultsCard? results = null;
        HintSheet? hints = null;
        Step("find overlays", () =>
        {
            var cards = new List<ResultsCard>();
            var sheets = new List<HintSheet>();
            Collect(node!, cards);
            Collect(node!, sheets);
            results = cards.FirstOrDefault();
            hints = sheets.FirstOrDefault();
        });

        Step("close results", () => results?.Close());
        await Wait(0.3);

        Step("open hint sheet", () => hints!.Open(40, 1, 2, true));
        await Wait(1.0);
        await Shot("06-hint-sheet");
        Step("close hint sheet", () => hints!.Close());
        await Wait(0.2);

        Step("open pause", () => screen!.OnBack());
        await Wait(0.8);
        await Shot("07-pause");
        Step("close pause", () => screen!.OnBack());
        await Wait(0.3);

        Step("forge exit", () =>
        {
            screen!.OnExit();
            node!.QueueFree();
        });
        await Wait(0.3);

        // -------------------------------------------------------- phase C: toybox
        Control? toybox = null;
        Step("build toybox", () =>
        {
            toybox = new Control { MouseFilter = MouseFilterEnum.Ignore };
            _host.AddChild(toybox);
            toybox.SetAnchorsPreset(LayoutPreset.FullRect);

            var column = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            column.AddThemeConstantOverride("separation", Tok.I(14f));
            toybox.AddChild(column);
            column.SetAnchorsPreset(LayoutPreset.FullRect);
            column.OffsetLeft = Tok.Dp(24f);
            column.OffsetRight = -Tok.Dp(24f);
            column.OffsetTop = Tok.Dp(160f);

            var runes = new List<RuneData>
            {
                new() { Id = "ember", Name = "Ember", Kind = RuneKind.Score, Rarity = RuneTier.Rare,
                        CoinDeltaPct = 0.10, Effect = "+1 coin per long letter" },
                new() { Id = "anvil", Name = "Anvil", Kind = RuneKind.Utility, Rarity = RuneTier.Uncommon,
                        Effect = "+1 free hint per forge" },
                new() { Id = "prism", Name = "Prism", Kind = RuneKind.Economy, Rarity = RuneTier.Common,
                        CoinDeltaPct = -0.05, Effect = "Bonus Jar counts double" },
            };
            var strip = new BuildStrip();
            column.AddChild(strip);
            strip.Refresh(runes, 3, true);

            var chips = Ui.MakeRow(8f);
            column.AddChild(chips);
            foreach (RuneData rune in runes) chips.AddChild(RuneChip.Create(rune, false));
            chips.AddChild(RuneChip.Create(null, true));

            var stars = Ui.MakeRow(8f);
            column.AddChild(stars);
            for (int i = 0; i < 3; i++)
            {
                StarIcon star = StarIcon.Create(28f);
                stars.AddChild(star);
                star.SetFilled(i < 2);
            }

            var glyphs = Ui.MakeRow(8f);
            column.AddChild(glyphs);
            foreach (Glyph glyph in Enum.GetValues<Glyph>())
                glyphs.AddChild(IconGlyph.Create(glyph, 22f, Tok.TextDim));

            var badges = Ui.MakeRow(8f);
            column.AddChild(badges);
            badges.AddChild(AdBadge.Create());
            badges.AddChild(Ui.MakeLabel("AD badge", 20f, Tok.Muted));

            var heat = new HeatBar();
            column.AddChild(heat);
            heat.CustomMinimumSize = new Vector2(0, Tok.Dp(10f));

            var flow = new FlowMeter();
            column.AddChild(flow);

            var rewarded = RewardedRow.Create("Watch a sponsor's message", "+1 Mark",
                                              () => GD.Print("[SMOKE] rewarded row"), 72f);
            column.AddChild(rewarded);
        });
        await Wait(0.2);
        Step("toybox animate", () =>
        {
            foreach (Node child in toybox!.GetChild(0).GetChildren())
            {
                switch (child)
                {
                    case HeatBar heat: heat.SetHeat(85, 2400, true); break;
                    case FlowMeter flow: flow.SetFlow(2.4, true); break;
                    case RewardedRow row: row.SetLoading(true); break;
                }
            }
        });
        await Wait(0.7);
        await Shot("08-toybox");
        toybox!.QueueFree();
        await Wait(0.2);

        // -------------------------------------------------------- phase D: screens
        RunEndInfo? endInfo = null;
        Step("run to end", () =>
        {
            ForgeOutcome? outcome = BuildOutcome(forge);
            if (outcome is null) throw new InvalidOperationException("no outcome");
            for (int i = 0; i < 12 && endInfo is null; i++)
                endInfo = RunService.Instance.ApplyForge(outcome, forge.DifficultyIndex);
            if (endInfo is null) throw new InvalidOperationException("run never ended");
            GD.Print($"[SMOKE] run end: {endInfo.WordsForged} words, {endInfo.Forges} forges");
        });

        await Screen("home", "res://ui/screens/Home.tscn", null);
        await Screen("draft", "res://ui/screens/RuneDraft.tscn", new DraftPayload(2));
        await Screen("runend", "res://ui/screens/RunEnd.tscn",
                     endInfo is null ? null : new RunEndPayload(endInfo, 7));
    }

    private static ForgeOutcome? BuildOutcome(ForgeData forge)
    {
        var session = new ForgeSession(forge, GameDataService.Instance.Dictionary,
                                       ConfigService.Instance.Config, RuneEffects.None);
        foreach (GridSlot slot in forge.Slots.Where(s => !s.IsKeystone))
            session.Submit(slot.Word.ToLowerInvariant());
        foreach (GridSlot slot in forge.Slots.Where(s => s.IsKeystone))
            session.Submit(slot.Word.ToLowerInvariant());
        return session.Outcome;
    }

    private async Task Screen(string label, string path, object? payload)
    {
        Node? node = null;
        Step($"screen {label}", () =>
        {
            var packed = GD.Load<PackedScene>(path);
            node = packed.Instantiate();
            _host.AddChild(node);
            if (node is not IScreen screen) throw new InvalidOperationException($"{label} is not IScreen");
            screen.OnEnter(payload);
        });
        await Wait(0.9);
        if (node is not null)
        {
            AuditFits(node);
            if (label is "runend" or "draft") Dump(node, 0);
        }
        await Shot($"09-{label}");
        Step($"screen {label} exit", () =>
        {
            if (node is null) return;
            ((IScreen)node).OnExit();
            node.QueueFree();
        });
        await Wait(0.2);
    }

    /// <summary>Taps real rack tiles in order until the rack spells <paramref name="word"/>.</summary>
    private static bool SpellWord(LetterRack rack, string word)
    {
        var tiles = new List<RackTile>();
        Collect(rack, tiles);
        tiles = tiles.OrderBy(t => t.TileIndex).ToList();

        var used = new HashSet<int>();
        foreach (char letter in word)
        {
            RackTile? pick = tiles.FirstOrDefault(t => !used.Contains(t.TileIndex) && t.Letter == letter);
            if (pick is null) return false;
            used.Add(pick.TileIndex);
            Tap(rack, pick);
        }
        return rack.CurrentWord == word;
    }

    private static void Tap(LetterRack rack, RackTile tile)
    {
        Vector2 global = tile.GetGlobalRect().GetCenter();
        Vector2 local = rack.GetGlobalTransformWithCanvas().AffineInverse() * global;

        rack._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = true, Position = local
        });
        rack._GuiInput(new InputEventMouseButton
        {
            ButtonIndex = MouseButton.Left, Pressed = false, Position = local
        });
    }

    /// <summary>Reports any Control that escapes the 1080x1920 design canvas.</summary>
    private static void AuditFits(Node node, string? label = null)
    {
        var bounds = new Rect2(0, 0, DesignW, DesignH);
        int bad = 0;
        Walk(node);
        GD.Print($"[FIT] {(label ?? node.GetType().Name)}: {bad} control(s) outside the canvas");

        void Walk(Node current)
        {
            // Content inside a ScrollContainer is ALLOWED to overflow: that is what
            // scrolling is for.
            if (current is ScrollContainer) return;
            foreach (Node child in current.GetChildren())
            {
                if (child is Control c && c.IsVisibleInTree())
                {
                    Rect2 rect = c.GetGlobalRect();
                    if (rect.End.X > bounds.End.X + 1f || rect.End.Y > bounds.End.Y + 1f ||
                        rect.Position.X < -1f || rect.Position.Y < -1f)
                    {
                        bad++;
                        if (bad <= 6)
                            GD.PushWarning($"[FIT] {c.GetType().Name} '{c.Name}' rect={rect} " +
                                           $"min={c.GetCombinedMinimumSize()}");
                    }
                }
                Walk(child);
            }
        }
    }

    private static void Dump(Node node, int depth)
    {
        if (depth > 4) return;
        if (node is Control control)
            GD.Print($"[TREE] {new string(' ', depth * 2)}{node.GetType().Name} " +
                     $"pos={control.Position} size={control.Size} min={control.GetCombinedMinimumSize()}");
        else
            GD.Print($"[TREE] {new string(' ', depth * 2)}{node.GetType().Name}");
        foreach (Node child in node.GetChildren()) Dump(child, depth + 1);
    }

    private static void Collect<T>(Node root, List<T> into) where T : class
    {
        foreach (Node child in root.GetChildren())
        {
            if (child is T match) into.Add(match);
            Collect(child, into);
        }
    }

    /// <summary>Grabs the rendered frame so the layout can be inspected offline.</summary>
    private async Task Shot(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Image image = _view.GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://_shots"));
        image.SavePng($"res://_shots/{name}.png");
        GD.Print($"[SMOKE] shot {name} {image.GetWidth()}x{image.GetHeight()}");
    }

    private void Step(string name, Action action)
    {
        _steps++;
        try
        {
            action();
        }
        catch (Exception e)
        {
            _failures++;
            GD.PushError($"[SMOKE] {name}: FAIL — {e.GetType().Name}: {e.Message}");
        }
    }

    private async Task Wait(double seconds)
        => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
}
