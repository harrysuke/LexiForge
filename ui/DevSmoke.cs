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

        GD.Print($"[SMOKE] mode | display={DisplayServer.GetName()} " +
                 $"| pngCapture={(Headless ? "off (assertion-only)" : "on")}");

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

        // UI-64: the reveal card must fit the 1080x1920 canvas with one word
        // already forged (mixed Jade check / Text dot / Gold keystone states).
        GiveUpModal? giveUp = null;
        Step("find give up card", () =>
        {
            var cards = new List<GiveUpModal>();
            Collect(node!, cards);
            giveUp = cards.FirstOrDefault() ?? throw new InvalidOperationException("no GiveUpModal");
        });
        Step("open give up", () =>
        {
            var peek = new ForgeSession(forge, GameDataService.Instance.Dictionary,
                                        ConfigService.Instance.Config, RuneEffects.None);
            GridSlot first = forge.Slots.First(s => !s.IsKeystone);
            peek.Submit(first.Word.ToLowerInvariant());
            giveUp!.Open(forge, peek);
        });
        await Wait(0.5);
        await Shot("06b-give-up");
        AuditFits(node!, "ForgeScreen/give-up");
        Step("close give up", () => giveUp!.Close());
        await Wait(0.2);

        // GDD 6.2.1 promises the card survives an 8-word forge. Shipped packs
        // only reach 4 words, so without a synthetic stress case that claim
        // would never be exercised — and LayoutCard() height-clamps silently.
        Step("give up 8-word stress", () =>
        {
            var stress = new ForgeData
            {
                ForgeId = 9999,
                Theme = "Emberworks",
                LetterBank = "starlight",
                SolutionWords = new List<string>
                {
                    "art", "rat", "tar", "star", "tsar", "arts", "rats", "starlight"
                },
                KeystoneWord = "starlight"
            };
            // Open() only reads FoundSolutions from the session; a real session
            // keeps the stress case honest about the shape of the data.
            var peek = new ForgeSession(forge, GameDataService.Instance.Dictionary,
                                        ConfigService.Instance.Config, RuneEffects.None);
            GridSlot first = forge.Slots.First(s => !s.IsKeystone);
            peek.Submit(first.Word.ToLowerInvariant());
            giveUp!.Open(stress, peek);
        });
        await Wait(0.5);
        await Shot("06c-give-up-8-words");
        AuditFits(node!, "ForgeScreen/give-up-8-words");
        Step("assert 8 chips inside card", () =>
        {
            var grids = new List<GridContainer>();
            Collect(giveUp!, grids);
            GridContainer grid = grids.First();
            PanelContainer card = giveUp!.GetChildren().OfType<PanelContainer>().First();
            Rect2 cardRect = card.GetGlobalRect();
            int visible = 0;
            foreach (Node child in grid.GetChildren())
            {
                if (child is not Control c || !c.IsVisibleInTree()) continue;
                visible++;
                Rect2 r = c.GetGlobalRect();
                if (r.Position.Y < cardRect.Position.Y - 1f || r.End.Y > cardRect.End.Y + 1f ||
                    r.Position.X < cardRect.Position.X - 1f || r.End.X > cardRect.End.X + 1f)
                    throw new InvalidOperationException(
                        $"chip {visible} escapes the card: {r} vs card {cardRect}");
            }
            if (visible != 8) throw new InvalidOperationException($"expected 8 chips, got {visible}");
            GD.Print($"[FIT] give-up 8-word: {visible} chips inside card " +
                     $"{cardRect.Size.X / Tok.Scale:F0}x{cardRect.Size.Y / Tok.Scale:F0} dp");
        });
        Step("close give up stress", () => giveUp!.Close());
        await Wait(0.2);

        // The real difficulty curve is 3 + 9*t, so late forges reach TWELVE
        // words. At that count the word list must scroll while every exit stays
        // pinned — otherwise LayoutCard()'s clamp silently hides the buttons.
        Step("give up 12-word stress", () =>
        {
            var stress = new ForgeData
            {
                ForgeId = 9998,
                Theme = "Observatory",
                LetterBank = "starlightxyz",
                SolutionWords = new List<string>
                {
                    "art", "rat", "tar", "star", "tsar", "arts",
                    "rats", "tars", "salt", "last", "trail", "starlight"
                },
                KeystoneWord = "starlight"
            };
            var peek = new ForgeSession(forge, GameDataService.Instance.Dictionary,
                                        ConfigService.Instance.Config, RuneEffects.None);
            GridSlot first = forge.Slots.First(s => !s.IsKeystone);
            peek.Submit(first.Word.ToLowerInvariant());
            giveUp!.Open(stress, peek);
        });
        await Wait(0.5);
        await Shot("06d-give-up-12-words");
        AuditFits(node!, "ForgeScreen/give-up-12-words");
        Step("assert 12-word exits stay pinned", () =>
        {
            var grids = new List<GridContainer>();
            Collect(giveUp!, grids);
            PanelContainer card = giveUp!.GetChildren().OfType<PanelContainer>().First();
            Rect2 cardRect = card.GetGlobalRect();
            if (cardRect.End.Y > DesignH - 1f || cardRect.Position.Y < -1f)
                throw new InvalidOperationException($"card outside canvas: {cardRect}");

            var buttons = new List<Button>();
            Collect(giveUp!, buttons);
            foreach (Button b in buttons)
            {
                Rect2 r = b.GetGlobalRect();
                if (r.End.Y > cardRect.End.Y + 1f || r.Position.Y < cardRect.Position.Y - 1f)
                    throw new InvalidOperationException($"exit '{b.Text}' escaped card: {r} vs {cardRect}");
            }
            if (buttons.Count != 3) throw new InvalidOperationException($"expected 3 exits, got {buttons.Count}");
            GD.Print($"[FIT] give-up 12-word: card {cardRect.Size.X / Tok.Scale:F0}x" +
                     $"{cardRect.Size.Y / Tok.Scale:F0} dp, {buttons.Count} exits pinned, scroll list active");
        });
        Step("close give up 12-word", () => giveUp!.Close());
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

        // ------------------------------------------- phase B0: Give Up answer regression
        // Player report: MAT, EAT, Give Up (reveals MEAT), KEEP FORGING, then
        // MEAT -> "Not in the dictionary". This replays the exact tap-to-spell
        // route through the REAL rack, REAL ForgeScreen session and REAL modal,
        // so a stale/partial selection after the reveal cannot hide in a unit test.
        await RunGiveUpAnswerRegression();

        // ------------------------------------------ phase B1: CARE / "Already forged"
        // Player report: ARC, ERA, Give Up (reveals CARE), KEEP FORGING, then an
        // attempt at CARE showed "Already forged". The play log proved CARE was
        // never submitted: 3-tile drags kept sending CAR/ACE/EAR, already in the
        // Bonus Jar, and a generic toast made that look like a refused answer.
        await RunRevealedKeystoneRegression();

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

    /// <summary>
    /// Regression for the "revealed word is not in the dictionary" report. Give Up
    /// must leave the rack and the ForgeSession byte-for-byte playable: after
    /// MAT + EAT the keystone MEAT must still be accepted. Every word is built by
    /// tapping real tiles, and the outcome is read from the real EventBus signals
    /// the shipped screen emits — no private fields, no production-only hooks.
    /// </summary>
    private async Task RunGiveUpAnswerRegression()
    {
        ForgeData forge = GameDataService.Instance.Forges
            .FirstOrDefault(f => f.ForgeId == 2)
            ?? throw new InvalidOperationException("forge 2 missing from shipped catalogue");
        GD.Print($"[REPRO] forge {forge.ForgeId} bank='{forge.LetterBank}' " +
                 $"words={string.Join(",", forge.SolutionWords)} keystone={forge.KeystoneWord}");
        GD.Print($"[REPRO] dictionary 'meat'={GameDataService.Instance.Dictionary.Contains("meat")} " +
                 $"'mat'={GameDataService.Instance.Dictionary.Contains("mat")} " +
                 $"'eat'={GameDataService.Instance.Dictionary.Contains("eat")} " +
                 $"gaps={GameDataService.Instance.DictionaryGaps.Count}");
        Step("repro: no pack/trie drift at boot", () =>
        {
            var gaps = GameDataService.Instance.DictionaryGaps;
            if (gaps.Count != 0)
                throw new InvalidOperationException("words_en.dat is missing shipped answers: " + string.Join(",", gaps));
        });

        var log = new List<string>();
        EventBus.WordAcceptedEventHandler onAccepted = (string w, int _, int _) => log.Add($"OK:{w}");
        EventBus.WordRejectedEventHandler onRejected = (string r) => log.Add($"BAD:{r}");
        EventBus.Instance.WordAccepted += onAccepted;
        EventBus.Instance.WordRejected += onRejected;

        Node? node = null;
        ForgeScreen? screen = null;
        LetterRack? rack = null;
        ForgeHud? hud = null;
        GiveUpModal? giveUp = null;
        LetterRack.WordSubmittedEventHandler? onSubmitted = null;

        try
        {
            Step("repro: forge 2 screen", () =>
            {
                var packed = GD.Load<PackedScene>("res://ui/screens/Forge.tscn");
                node = packed.Instantiate();
                _host!.AddChild(node);
                screen = (ForgeScreen)node;
                screen.ResetFor(forge, 0, RuneEffects.None);
                screen.OnEnter(null);

                var racks = new List<LetterRack>();
                var huds = new List<ForgeHud>();
                var cards = new List<GiveUpModal>();
                Collect(node, racks);
                Collect(node, huds);
                Collect(node, cards);
                rack = racks.First();
                hud = huds.First();
                giveUp = cards.First();
            });
            await Wait(0.5);

            onSubmitted = (string w) => log.Add($"SUBMIT:{w}");
            rack!.WordSubmitted += onSubmitted;

            void Spell(string word)
            {
                Step($"repro: spell '{word}'", () =>
                {
                    if (!SpellWord(rack, word))
                        throw new InvalidOperationException($"rack spells '{rack.CurrentWord}', wanted '{word}'");
                });
                Step($"repro: submit '{word}'", () => rack.SubmitCurrent());
            }

            Spell("mat");
            Spell("eat");
            GD.Print("[REPRO] after MAT + EAT: " + string.Join(" ", log));

            // UI-P2 promises the strip turns Rust on rejection. ClearSelection()
            // used to wipe the text first, so the player could not see which word
            // the game actually received — the confusion behind this bug report.
            Step("repro: rejected word stays visible in the compose strip", () =>
            {
                if (!SpellWord(rack!, "ema")) throw new InvalidOperationException("cannot spell 'ema'");
                rack.SubmitCurrent();
                var labels = new List<Label>();
                Collect(rack, labels);
                if (!labels.Any(l => l.Text == "EMA"))
                    throw new InvalidOperationException("rejected word missing from strip: " +
                                                        string.Join(" | ", labels.Select(l => l.Text)));
            });
            await Wait(0.5);

            Step("repro: open give up", () => hud!.EmitSignal(ForgeHud.SignalName.GiveUpRequested));
            await Wait(0.3);
            Step("repro: card is open", () =>
            {
                if (!giveUp!.IsOpen) throw new InvalidOperationException("Give Up card did not open");
            });
            Step("repro: card shows MEAT", () =>
            {
                var labels = new List<Label>();
                Collect(giveUp!, labels);
                if (!labels.Any(l => l.Text == "MEAT"))
                    throw new InvalidOperationException("keystone MEAT not shown in card: " +
                                                        string.Join(" | ", labels.Select(l => l.Text)));
            });

            Step("repro: keep forging", () => giveUp!.EmitSignal(GiveUpModal.SignalName.KeepForgingRequested));
            await Wait(0.3);
            Step("repro: card is closed", () =>
            {
                if (giveUp!.IsOpen) throw new InvalidOperationException("Give Up card did not close");
            });

            Spell("meat");
            GD.Print("[REPRO] final: " + string.Join(" ", log));
            Step("repro: revealed keystone MEAT is accepted", () =>
            {
                if (!log.Contains("OK:meat"))
                    throw new InvalidOperationException("MEAT was rejected after Give Up — " + string.Join(" ", log));
                // Exactly one rejection is expected: the deliberate 'ema' typo
                // used to verify the strip. No new one may appear after the reveal.
                if (log.Count(x => x.StartsWith("BAD:", StringComparison.Ordinal)) != 1)
                    throw new InvalidOperationException("unexpected rejection — " + string.Join(" ", log));
            });

            // The shipped trie is currently complete, so the real screen above
            // cannot prove the DEFENCE. Replay the same forge against a trie that
            // deliberately omits "meat" (a stale words_en.dat): solution words
            // must still win, while optional bonus words stay trie-gated.
            Step("repro: stale words_en.dat still accepts every solution", () =>
            {
                var stale = new WordTrie();
                foreach (string w in new[] { "eat", "mat", "team", "tame", "tea", "ate" }) stale.Add(w);
                var s = new ForgeSession(forge, stale, ConfigService.Instance.Config, RuneEffects.None);
                string trail = string.Join(",", new[] { "mat", "eat", "meat" }
                    .Select(w => $"{w}:{s.Submit(w).Kind}"));
                GD.Print($"[REPRO] stale trie (no 'meat'): {trail} completed={s.Completed}");
                if (!s.Completed)
                    throw new InvalidOperationException("stale trie blocked a revealed answer — " + trail);

                var bonus = new ForgeSession(forge, stale, ConfigService.Instance.Config, RuneEffects.None);
                if (bonus.Submit("ema").Kind != WordSubmissionKind.NotInDictionary)
                    throw new InvalidOperationException("trie no longer gates optional bonus words");
                if (bonus.Submit("team").Kind != WordSubmissionKind.BonusWord)
                    throw new InvalidOperationException("valid dictionary bonus word was rejected");
            });
        }
        finally
        {
            if (onSubmitted is not null && rack is not null) rack.WordSubmitted -= onSubmitted;
            EventBus.Instance.WordAccepted -= onAccepted;
            EventBus.Instance.WordRejected -= onRejected;
            if (screen is not null) screen.OnExit();
            if (node is not null) node.QueueFree();
        }
        await Wait(0.2);
    }

    /// <summary>
    /// Regression for the "CARE / Already forged" report. Replays forge 3 through
    /// the REAL rack, ForgeSession, Give Up card and toast. The player had forged
    /// ARC + ERA, CAR was already in the Bonus Jar, and a 3-tile drag resubmitted
    /// CAR while they meant the 4-letter keystone. CARE itself was never sent.
    ///
    /// Asserts the two fixes: the toast names the word it actually received and
    /// never borrows solution-word language (UI-66), and a repeat keeps the tiles
    /// selected so one more rune completes the revealed answer (UI-65).
    /// </summary>
    private async Task RunRevealedKeystoneRegression()
    {
        ForgeData forge = GameDataService.Instance.Forges
            .FirstOrDefault(f => f.ForgeId == 3)
            ?? throw new InvalidOperationException("forge 3 missing from shipped catalogue");
        GD.Print($"[CARE] forge {forge.ForgeId} bank='{forge.LetterBank}' " +
                 $"words={string.Join(",", forge.SolutionWords)} keystone={forge.KeystoneWord} " +
                 $"gaps={GameDataService.Instance.DictionaryGaps.Count}");

        var log = new List<string>();
        EventBus.WordAcceptedEventHandler onAccepted = (string w, int _, int _) => log.Add($"OK:{w}");
        EventBus.WordRejectedEventHandler onRejected = (string r) => log.Add($"BAD:{r}");
        EventBus.Instance.WordAccepted += onAccepted;
        EventBus.Instance.WordRejected += onRejected;

        Node? node = null;
        ForgeScreen? screen = null;
        LetterRack? rack = null;
        ForgeHud? hud = null;
        GiveUpModal? giveUp = null;
        Toast? toast = null;
        LetterRack.WordSubmittedEventHandler? onSubmitted = null;

        try
        {
            Step("care: forge 3 screen", () =>
            {
                var packed = GD.Load<PackedScene>("res://ui/screens/Forge.tscn");
                node = packed.Instantiate();
                _host!.AddChild(node);
                screen = (ForgeScreen)node;
                screen.ResetFor(forge, 0, RuneEffects.None);
                screen.OnEnter(null);

                var racks = new List<LetterRack>();
                var huds = new List<ForgeHud>();
                var cards = new List<GiveUpModal>();
                var toasts = new List<Toast>();
                Collect(node, racks);
                Collect(node, huds);
                Collect(node, cards);
                Collect(node, toasts);
                rack = racks.First();
                hud = huds.First();
                giveUp = cards.First();
                toast = toasts.First();
            });
            await Wait(0.5);

            onSubmitted = (string w) => log.Add($"SUBMIT:{w}");
            rack!.WordSubmitted += onSubmitted;

            void Spell(string word)
            {
                Step($"care: spell '{word}'", () =>
                {
                    if (!SpellWord(rack, word))
                        throw new InvalidOperationException($"rack spells '{rack.CurrentWord}', wanted '{word}'");
                });
                Step($"care: submit '{word}'", () => rack.SubmitCurrent());
            }

            Spell("arc");
            Spell("era");
            Spell("car"); // accepted into the Bonus Jar, exactly as in the play log
            GD.Print("[CARE] after ARC + ERA + CAR: " + string.Join(" ", log));

            Step("care: open give up", () => hud!.EmitSignal(ForgeHud.SignalName.GiveUpRequested));
            await Wait(0.3);
            Step("care: card reveals CARE", () =>
            {
                if (!giveUp!.IsOpen) throw new InvalidOperationException("Give Up card did not open");
                var labels = new List<Label>();
                Collect(giveUp, labels);
                if (!labels.Any(l => l.Text == "CARE"))
                    throw new InvalidOperationException("keystone CARE not shown: " +
                                                        string.Join(" | ", labels.Select(l => l.Text)));
            });
            Step("care: keep forging", () => giveUp!.EmitSignal(GiveUpModal.SignalName.KeepForgingRequested));
            await Wait(0.3);

            // The failing action: a 3-tile drag submits CAR again. Before the fix
            // this produced a wordless "Already forged" toast and wiped the rack.
            Step("care: re-submit the 3-letter prefix CAR", () =>
            {
                if (!SpellWord(rack!, "car")) throw new InvalidOperationException("cannot spell 'car'");
                rack.SubmitCurrent();
            });
            await Wait(0.3);
            GD.Print("[CARE] after duplicate: " + string.Join(" ", log));

            Step("care: repeat names the word and never says 'Already forged'", () =>
            {
                string text = toast!.CurrentText;
                GD.Print($"[CARE] toast='{text}'");
                if (!text.Contains("CAR", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("toast does not name the rejected word: " + text);
                if (text.Contains("Already forged", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("toast still borrows solution-word language: " + text);
                if (!text.Contains("jar", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("toast does not say where the word already is: " + text);
            });
            Step("care: repeat is not a penalty", () =>
            {
                if (log.Any(x => x.StartsWith("BAD:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("duplicate emitted a word rejection — " + string.Join(" ", log));
            });
            Step("care: repeat keeps the tiles selected", () =>
            {
                if (rack!.CurrentWord != "car")
                    throw new InvalidOperationException($"selection was wiped: '{rack.CurrentWord}'");
            });

            // Recovery: one more rune turns the retained prefix into the answer.
            Step("care: add the missing E rune", () =>
            {
                if (!TapLetter(rack!, 'e')) throw new InvalidOperationException("no unused E tile");
                if (rack.CurrentWord != "care")
                    throw new InvalidOperationException($"expected CARE, rack spells '{rack.CurrentWord}'");
            });
            Step("care: submit CARE", () => rack!.SubmitCurrent());
            await Wait(0.4);
            GD.Print("[CARE] final: " + string.Join(" ", log));
            Step("care: revealed keystone completes the forge", () =>
            {
                if (!log.Contains("OK:care"))
                    throw new InvalidOperationException("CARE was rejected after Give Up — " + string.Join(" ", log));
                if (log.Any(x => x.StartsWith("BAD:", StringComparison.Ordinal)))
                    throw new InvalidOperationException("unexpected penalty — " + string.Join(" ", log));
            });
        }
        finally
        {
            if (onSubmitted is not null && rack is not null) rack.WordSubmitted -= onSubmitted;
            EventBus.Instance.WordAccepted -= onAccepted;
            EventBus.Instance.WordRejected -= onRejected;
            if (screen is not null) screen.OnExit();
            if (node is not null) node.QueueFree();
        }
        await Wait(0.2);
    }

    /// <summary>Taps one still-unselected rack tile carrying <paramref name="letter"/>.</summary>
    private static bool TapLetter(LetterRack rack, char letter)
    {
        var tiles = new List<RackTile>();
        Collect(rack, tiles);
        RackTile? pick = tiles.OrderBy(t => t.TileIndex).FirstOrDefault(t =>
            char.ToLowerInvariant(t.Letter) == char.ToLowerInvariant(letter) &&
            t.State != RackTile.TileState.Selected);
        if (pick is null) return false;
        Tap(rack, pick);
        return true;
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

    /// <summary>
    /// True when there is no real display server, or the caller explicitly asked
    /// for assertion-only mode with <c>-- --no-shots</c>.
    ///
    /// IMPORTANT: do NOT detect this from OS.GetCmdlineArgs(). Godot strips every
    /// engine flag it recognises, so under <c>--headless</c> that array contained
    /// only the scene path "res://ui/DevSmoke.tscn". The old check therefore
    /// returned false, Shot() awaited FramePostDraw (which the dummy
    /// RenderingServer never emits) and the whole harness deadlocked after the
    /// first tree dump. DisplayServer.GetName() is the documented, authoritative
    /// signal: it returns "headless" exactly when --headless was used.
    /// </summary>
    private static bool Headless
    {
        get
        {
            if (string.Equals(DisplayServer.GetName(), "headless",
                              StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string arg in OS.GetCmdlineUserArgs())
                if (arg.Contains("no-shots", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    /// <summary>
    /// Grabs the rendered frame so the layout can be inspected offline. Headless /
    /// CI runs skip PNGs but keep every Step and AuditFits assertion. Even on a
    /// real renderer the frame wait is bounded, so a lost frame can never hang the
    /// harness again.
    /// </summary>
    private async Task Shot(string name)
    {
        if (Headless)
        {
            GD.Print($"[SMOKE] shot {name}: skipped (no display server; assertions still run)");
            return;
        }

        if (!await WaitForFrame(1.0))
        {
            GD.PushWarning($"[SMOKE] shot {name}: no FramePostDraw within 1s — PNG skipped");
            return;
        }

        Image image = _view.GetTexture().GetImage();
        DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath("res://_shots"));
        image.SavePng($"res://_shots/{name}.png");
        GD.Print($"[SMOKE] shot {name} {image.GetWidth()}x{image.GetHeight()}");
    }

    /// <summary>
    /// Races the draw callback against a SceneTreeTimer; returns true if a frame
    /// arrived first. ToSignal() returns a SignalAwaiter, not a Task, so each side
    /// is wrapped in an async Task before Task.WhenAny can race them.
    /// </summary>
    private async Task<bool> WaitForFrame(double timeoutSeconds)
    {
        Task frame = AwaitDraw();
        Task timeout = AwaitTimer(timeoutSeconds);
        return await Task.WhenAny(frame, timeout) == frame;
    }

    private async Task AwaitDraw()
        => await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);

    private async Task AwaitTimer(double seconds)
        => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

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
