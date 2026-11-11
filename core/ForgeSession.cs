using System;
using System.Collections.Generic;
using System.Linq;

namespace LexiForge.Core;

public enum WordSubmissionKind
{
    Invalid,
    TooShort,
    NotConstructible,
    NotInDictionary,
    AlreadyFound,
    BonusJarFull,
    BonusWord,
    SolutionWord,
    KeystoneLocked,
    ForgeAlreadyComplete
}

public enum HintSource { Scripted, Free, Coin, Ad, Nudge }

public readonly record struct WordSubmission(
    WordSubmissionKind Kind,
    string Word,
    double Flow,
    int BonusWords,
    int SolutionWordsFound,
    int SlotIndex,
    bool ForgeCompleted)
{
    public bool IsSuccess => Kind is WordSubmissionKind.BonusWord or WordSubmissionKind.SolutionWord;
    public bool IsPenalty => Kind is WordSubmissionKind.Invalid or WordSubmissionKind.TooShort
        or WordSubmissionKind.NotConstructible or WordSubmissionKind.NotInDictionary;
}

public readonly record struct HintResult(
    bool Granted, HintSource Source, int CoinCost, char Letter, int SlotIndex, int LetterIndex, string Reason);

/// <summary>Everything the results card, RunService and analytics need after a forge.</summary>
public sealed class ForgeOutcome
{
    public required ForgeResult Result { get; init; }
    public required int RuneBonusCoins { get; init; }
    public required int StarCoins { get; init; }
    public required int TotalCoins { get; init; }
    public required int CoolStrikes { get; init; }
    public required IReadOnlyList<string> BonusWords { get; init; }
    public int ForgeId { get; init; }
    public double SolveSeconds => Result.SolveSeconds;
    public double ParSeconds => Result.ParSeconds;
    public bool Perfect => Result.Perfect;
}

/// <summary>
/// The single authoritative forge state machine. PURE C# — no Godot, no scene tree,
/// no clock of its own. The view calls Tick(delta) and renders state; every rule
/// here is unit-testable in a console harness and reusable in the CI solver.
///
/// Design invariants enforced:
///  - No countdown. Elapsed time only feeds par-time stars and the free nudge.
///  - A wrong word resets Flow to 1.0x and nothing else (no punishment spirals).
///  - The keystone cannot be submitted until every other slot is filled.
///  - Bonus words are capped, never gated, and never consume the clean streak.
///  - All coin maths goes through EconomyModel so the balance simulator stays exact.
/// </summary>
public sealed class ForgeSession
{
    public const int MinWordLength = 3;

    private readonly ForgeData _forge;
    private readonly WordTrie _dictionary;
    private readonly EconomyTuning _economy;
    private readonly AntiFrustrationConfig _antiFrustration;
    private readonly RuneEffects _effects;
    private readonly HashSet<string> _solutions;
    private readonly HashSet<string> _found = new(StringComparer.Ordinal);
    private readonly List<string> _bonusJar = new();
    private readonly Dictionary<int, HashSet<int>> _revealed = new();
    private readonly string _bankWithWilds;

    private int _cleanStreak;
    private int _repeatedLetterWords;
    private int _emberLongLetters;
    private bool _hintSinceLastFind;
    private int _coolStrikes;
    private bool _anyFind;
    private double _firstWordFlow = 1.0;
    private double _lastCorrectAt;
    private int _nudgesInWindow;
    private double _nudgeWindowStart;
    private bool _struggleRevealUsed;
    private ForgeOutcome? _outcome;

    public ForgeSession(ForgeData forge, WordTrie dictionary, BalanceConfig cfg, RuneEffects effects)
    {
        _forge = forge ?? throw new ArgumentNullException(nameof(forge));
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _economy = cfg?.Economy ?? throw new ArgumentNullException(nameof(cfg));
        _antiFrustration = cfg.AntiFrustration;
        _effects = effects ?? RuneEffects.None;
        _solutions = new HashSet<string>(
            forge.SolutionWords.Select(w => w.ToLowerInvariant()), StringComparer.Ordinal);

        if (_solutions.Count == 0) throw new ArgumentException("Forge has no solution words.", nameof(forge));
        if (forge.KeystoneWord.Length == 0 ||
            !_solutions.Contains(forge.KeystoneWord.ToLowerInvariant()))
            throw new ArgumentException("KeystoneWord must be one of the solution words.", nameof(forge));
        if (forge.SolutionWords.Count > 0 &&
            !string.Equals(forge.SolutionWords[^1], forge.KeystoneWord, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Keystone must be the LAST SolutionWord (GDD 3.2).", nameof(forge));

        _bankWithWilds = forge.LetterBank.ToLowerInvariant() + new string('*', _effects.WildTiles);
    }

    // ------------------------------------------------------------------ state
    public ForgeData Forge => _forge;
    public double Flow { get; private set; } = 1.0;
    public double FlowPeak { get; private set; } = 1.0;
    public int CleanStreak => _cleanStreak;
    public int WrongAttempts { get; private set; }
    public int HintsUsed { get; private set; }
    public int FreeHintsRemaining { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public bool Completed => _outcome is not null;
    public IReadOnlyCollection<string> FoundSolutions => _found;
    public IReadOnlyList<string> BonusJar => _bonusJar;
    public int SolutionWordCount => _solutions.Count;
    public bool KeystoneUnlocked => _found.Count >= _solutions.Count - 1;
    public ForgeOutcome? Outcome => _outcome;
    public RuneEffects Effects => _effects;

    /// <summary>Anvil / scripted hints are granted at construction by the caller.</summary>
    public void GrantFreeHints(int count) => FreeHintsRemaining = Math.Max(0, count);

    public void Tick(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || _outcome is not null) return;
        ElapsedSeconds += deltaSeconds;
    }

    // -------------------------------------------------------------- submission
    public WordSubmission Submit(string rawWord)
    {
        if (_outcome is not null)
            return new WordSubmission(WordSubmissionKind.ForgeAlreadyComplete, "", Flow, _bonusJar.Count, _found.Count, -1, true);

        string word = Normalize(rawWord);
        if (word.Length == 0)
            return Penalty(WordSubmissionKind.Invalid, word);
        if (word.Length < MinWordLength)
            return Penalty(WordSubmissionKind.TooShort, word);
        if (!IsConstructible(word, _bankWithWilds))
            return Penalty(WordSubmissionKind.NotConstructible, word);
        if (!_dictionary.Contains(word))
            return Penalty(WordSubmissionKind.NotInDictionary, word);

        if (_solutions.Contains(word))
        {
            if (_found.Contains(word))
                return new WordSubmission(WordSubmissionKind.AlreadyFound, word, Flow, _bonusJar.Count, _found.Count, -1, false);

            string keystone = _forge.KeystoneWord.ToLowerInvariant();
            if (word == keystone && !KeystoneUnlocked)
                return new WordSubmission(WordSubmissionKind.KeystoneLocked, word, Flow, _bonusJar.Count, _found.Count,
                                          SlotIndexFor(word), false);

            _found.Add(word);
            _cleanStreak++;
            _lastCorrectAt = ElapsedSeconds;
            double cap = _effects.EffectiveFlowCap(Math.Max(1.0, _economy.FlowCap), _repeatedLetterWords);
            Flow = 1.0 + Math.Min(cap - 1.0, Math.Max(0, _cleanStreak) * _economy.FlowStep);
            FlowPeak = Math.Max(FlowPeak, Flow);
            if (!_hintSinceLastFind) _coolStrikes++;
            _hintSinceLastFind = false;

            if (!_anyFind) { _anyFind = true; _firstWordFlow = Flow; }
            if (HasRepeatedLetter(word)) _repeatedLetterWords++;
            if (word.Length >= 5) _emberLongLetters += word.Length;

            bool complete = _found.Count == _solutions.Count;
            if (complete) _outcome = BuildOutcome();
            return new WordSubmission(WordSubmissionKind.SolutionWord, word, Flow, _bonusJar.Count,
                                      _found.Count, SlotIndexFor(word), complete);
        }

        if (_bonusJar.Contains(word, StringComparer.Ordinal))
            return new WordSubmission(WordSubmissionKind.AlreadyFound, word, Flow, _bonusJar.Count, _found.Count, -1, false);
        if (_bonusJar.Count >= _economy.BonusWordCap)
            return new WordSubmission(WordSubmissionKind.BonusJarFull, word, Flow, _bonusJar.Count, _found.Count, -1, false);

        _bonusJar.Add(word);
        return new WordSubmission(WordSubmissionKind.BonusWord, word, Flow, _bonusJar.Count, _found.Count, -1, false);
    }

    private WordSubmission Penalty(WordSubmissionKind kind, string word)
    {
        // A wrong word must never feel like a loss: Flow resets, nothing else does.
        WrongAttempts++;
        _cleanStreak = 0;
        Flow = 1.0;
        return new WordSubmission(kind, word, Flow, _bonusJar.Count, _found.Count, -1, false);
    }

    // ------------------------------------------------------------------- hints
    /// <param name="coinsAvailable">Only consulted for HintSource.Coin; the caller performs the actual debit.</param>
    public HintResult UseHint(HintSource source, int coinsAvailable = int.MaxValue)
    {
        if (_outcome is not null) return new HintResult(false, source, 0, '\0', -1, -1, "forge_complete");
        if (HintsUsed >= _economy.MaxHintsPerForge)
            return new HintResult(false, source, 0, '\0', -1, -1, "max_hints_reached");

        int cost = 0;
        if (source == HintSource.Coin)
        {
            cost = EconomyModel.HintCost(HintsUsed + 1, _economy);
            if (coinsAvailable < cost) return new HintResult(false, source, cost, '\0', -1, -1, "insufficient_coins");
        }
        else if ((source is HintSource.Free or HintSource.Scripted) && FreeHintsRemaining <= 0)
        {
            // Free/scripted hints are only free while the Anvil/tutorial budget lasts.
            return new HintResult(false, source, 0, '\0', -1, -1, "no_free_hints");
        }

        var target = FindHintTarget();
        if (target.slot < 0) return new HintResult(false, source, cost, '\0', -1, -1, "nothing_to_reveal");

        _revealed.TryAdd(target.slot, new HashSet<int>());
        _revealed[target.slot].Add(target.index);
        HintsUsed++;
        _hintSinceLastFind = true;
        if (source is HintSource.Free or HintSource.Scripted) FreeHintsRemaining--;

        return new HintResult(true, source, cost, target.letter, target.slot, target.index, "ok");
    }

    /// <summary>
    /// Free nudge after 45 s with no correct find. Automatic, once per 45 s window,
    /// no ad, no coin cost, and deliberately NOT counted as a hint (no star penalty).
    /// </summary>
    public bool TryGetNudge(out char letter, out int slotIndex, out int letterIndex)
    {
        letter = '\0'; slotIndex = -1; letterIndex = -1;
        if (_outcome is not null) return false;
        double window = _antiFrustration.FreeNudgeAfterSeconds;
        if (ElapsedSeconds - _lastCorrectAt < window) return false;
        if (ElapsedSeconds - _nudgeWindowStart >= window) { _nudgeWindowStart = ElapsedSeconds; _nudgesInWindow = 0; }
        if (_nudgesInWindow >= _antiFrustration.FreeNudgePerWindow) return false;

        // Three wrong attempts in a row also unlocks an optional free letter reveal.
        var target = FindHintTarget();
        if (target.slot < 0) return false;

        _revealed.TryAdd(target.slot, new HashSet<int>());
        _revealed[target.slot].Add(target.index);
        _nudgesInWindow++;
        letter = target.letter; slotIndex = target.slot; letterIndex = target.index;
        return true;
    }

    /// <summary>After 3 wrong attempts the view may offer a free reveal.</summary>
    public bool ConsecutiveWrongAttemptsReached
        => !_struggleRevealUsed && WrongAttempts >= _antiFrustration.FreeLetterRevealAfterWrongAttempts;

    /// <summary>
    /// One-off free letter reveal after repeated wrong attempts (GDD 6.2). Uses
    /// the same reveal ledger as hints but deliberately does NOT increment
    /// HintsUsed — frustration relief must never cost the player a star.
    /// </summary>
    public bool TryGrantStruggleReveal(out char letter, out int slotIndex, out int letterIndex)
    {
        letter = '\0'; slotIndex = -1; letterIndex = -1;
        if (_outcome is not null || !ConsecutiveWrongAttemptsReached) return false;
        var target = FindHintTarget();
        if (target.slot < 0) return false;

        _revealed.TryAdd(target.slot, new HashSet<int>());
        _revealed[target.slot].Add(target.index);
        _struggleRevealUsed = true;
        letter = target.letter; slotIndex = target.slot; letterIndex = target.index;
        return true;
    }

    private (int slot, int index, char letter) FindHintTarget()
    {
        for (int s = 0; s < _forge.Slots.Count; s++)
        {
            var slot = _forge.Slots[s];
            string word = slot.Word.ToLowerInvariant();
            if (_found.Contains(word)) continue;
            if (word == _forge.KeystoneWord.ToLowerInvariant() && !KeystoneUnlocked) continue;

            _revealed.TryGetValue(s, out var shown);
            for (int i = 0; i < word.Length; i++)
                if (shown is null || !shown.Contains(i)) return (s, i, word[i]);
        }
        return (-1, -1, '\0');
    }

    // ------------------------------------------------------------- view helpers
    public bool IsSlotFilled(int slotIndex)
        => slotIndex >= 0 && slotIndex < _forge.Slots.Count
           && _found.Contains(_forge.Slots[slotIndex].Word.ToLowerInvariant());

    /// <summary>Lens rune: unfilled slot lengths are only visible when it is drafted.</summary>
    public bool IsSlotLengthVisible(int slotIndex)
        => _effects.RevealSlotLengths || IsSlotFilled(slotIndex);

    public string MaskFor(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= _forge.Slots.Count) return "";
        var slot = _forge.Slots[slotIndex];
        string word = slot.Word.ToLowerInvariant();
        if (IsSlotFilled(slotIndex)) return word.ToUpperInvariant();
        _revealed.TryGetValue(slotIndex, out var shown);
        return new string(word.Select((c, i) => shown is not null && shown.Contains(i) ? char.ToUpperInvariant(c) : '_').ToArray());
    }

    public char[] ShuffledTiles(int seed)
    {
        char[] tiles = _bankWithWilds.ToCharArray();
        var rng = new Random(seed);
        for (int i = tiles.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
        }
        return tiles;
    }

    public int SlotIndexFor(string word)
        => _forge.Slots.FindIndex(s => string.Equals(s.Word, word, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------------ results
    private ForgeOutcome BuildOutcome()
    {
        int stars = EconomyModel.Stars(HintsUsed, WrongAttempts, ElapsedSeconds,
                                       _forge.ParTimeSeconds, _economy);
        bool perfect = stars == 3;
        double flow = Math.Clamp(FlowPeak, 1.0, Math.Max(1.0, _economy.FlowCap));
        int bonusWords = Math.Min(_bonusJar.Count, _economy.BonusWordCap);

        // Canonical formula — identical to what BalanceSim runs.
        int baseCoins = EconomyModel.LevelCoins(_solutions.Count, bonusWords, flow,
                                                _effects.CoinDeltaPct, _economy);

        // Rune-specific additive bonuses stay OUT of the canonical formula so a
        // tuning change can never drift between simulator and shipped game.
        double prismExtra = bonusWords * _economy.PerBonusWord * flow
                            * (_effects.BonusJarMultiplier - 1.0) * (1.0 + _effects.CoinDeltaPct);
        double emberExtra = _emberLongLetters * _effects.EmberCoinsPerLongLetter;
        double sparkExtra = (_effects.FirstWordCoinMultiplier - 1.0) * _economy.PerSolutionWord * _firstWordFlow;
        int runeBonus = (int)Math.Round(prismExtra + emberExtra + sparkExtra);

        int starCoins = (int)Math.Round(EconomyModel.StarBonus(stars, _economy) * _effects.StarCoinMultiplier);
        int total = baseCoins + runeBonus + starCoins;

        var result = new ForgeResult(total, stars, FlowPeak, bonusWords, HintsUsed,
                                     ElapsedSeconds, _forge.ParTimeSeconds, WrongAttempts, perfect);

        return new ForgeOutcome
        {
            Result = result,
            RuneBonusCoins = runeBonus,
            StarCoins = starCoins,
            TotalCoins = total,
            CoolStrikes = CountCoolStrikes(),
            BonusWords = _bonusJar.ToArray(),
            ForgeId = _forge.ForgeId
        };
    }

    /// <summary>
    /// GDD 3.1 describes a -6 Heat "cool strike" per hint-free find, but the
    /// canonical Heat formula (GDD 3.3 / LevelShape.cs / BalanceSim) has no such
    /// term. Adding it would silently diverge from the 10k-player projection, so
    /// we expose the count for telemetry/UI only and leave the formula untouched
    /// pending a design decision. Quill's "+2 clean cools" is applied in RunState
    /// as a small, explicit perfect-forge cooling.
    /// </summary>
    private int CountCoolStrikes() => _coolStrikes;

    // ------------------------------------------------------------------ statics
    public static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        char[] buffer = new char[raw.Length];
        int n = 0;
        foreach (char c in raw)
        {
            char lower = char.ToLowerInvariant(c);
            if (lower is < 'a' or > 'z') return "";
            buffer[n++] = lower;
        }
        return new string(buffer, 0, n);
    }

    /// <summary>One tile per letter; '*' is a Gale wild that substitutes for exactly one letter.</summary>
    public static bool IsConstructible(ReadOnlySpan<char> word, ReadOnlySpan<char> bank)
    {
        var counts = new int[26];
        int wilds = 0;
        foreach (char raw in bank)
        {
            char c = char.ToLowerInvariant(raw);
            if (c == '*') wilds++;
            else if (c is >= 'a' and <= 'z') counts[c - 'a']++;
        }
        foreach (char raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (c is < 'a' or > 'z') return false;
            int idx = c - 'a';
            if (counts[idx] > 0) counts[idx]--;
            else if (wilds > 0) wilds--;
            else return false;
        }
        return true;
    }

    public static bool HasRepeatedLetter(string word)
        => word.Length > 1 && word.Distinct().Count() < word.Length;
}
