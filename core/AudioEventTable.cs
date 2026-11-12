using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LexiForge.Core;

/// <summary>
/// Pure-C# audio catalogue. Lives in core/ for the same reason BalanceConfig
/// does: it is linked verbatim into the Godot project AND the offline tools, so
/// the shipped runtime, CoreTests and the greybox asset generator can never
/// disagree about an event id, a bus name or an asset path.
///
/// This file has NO `using Godot;`. It describes WHAT should play; the Godot
/// runtime (audio/AudioDirector.cs) decides HOW.
/// </summary>

public enum AudioVariationMode { RoundRobinNoRepeat, ByMagnitude, Random }
public enum AudioPitchMode { Fixed, Jitter, Ladder }

public sealed class AudioBusDef
{
    public string Name { get; set; } = "";
    public double VolumeDb { get; set; }
    public string Send { get; set; } = "Master";
    public string Note { get; set; } = "";
}

public sealed class AudioDuckDefaultsDef
{
    public string Bus { get; set; } = "Music";
    public int AttackMs { get; set; }
    public int HoldMs { get; set; }
    public int ReleaseMs { get; set; }
}

public sealed class AudioDuckDef
{
    public string Bus { get; set; } = "Music";
    public double DepthDb { get; set; }
    public int AttackMs { get; set; }
    public int HoldMs { get; set; }
    public int ReleaseMs { get; set; }
}

public sealed class AudioPitchDef
{
    public AudioPitchMode Mode { get; set; } = AudioPitchMode.Fixed;
    public double[] Semitones { get; set; } = Array.Empty<double>();
    public int MagnitudeOffset { get; set; }
    public int MagnitudeDivisor { get; set; } = 1;
    public double JitterCents { get; set; }

    /// <summary>
    /// Ladder index derived from the semantic magnitude carried by
    /// EventBus.FeedbackCue. For tile.pick that is the 1-based letter index in
    /// the word; for word.solution it is Flow in hundredths (100..250).
    /// </summary>
    public int LadderIndex(int magnitude)
    {
        if (Semitones.Length == 0 || MagnitudeDivisor <= 0) return 0;
        int index = (magnitude - MagnitudeOffset) / MagnitudeDivisor;
        return Math.Clamp(index, 0, Semitones.Length - 1);
    }

    /// <summary>PitchScale for Godot's AudioStreamPlayer. jitterUnit is in [-1,1].</summary>
    public float PitchScale(int magnitude, double jitterUnit)
    {
        double semitones = Mode == AudioPitchMode.Ladder && Semitones.Length > 0
            ? Semitones[LadderIndex(magnitude)]
            : 0.0;
        semitones += JitterCents / 100.0 * jitterUnit;
        return (float)Math.Pow(2.0, semitones / 12.0);
    }
}

public sealed class AudioEventDef
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Bus { get; set; } = "SFX";
    public int Priority { get; set; }
    public string[] Streams { get; set; } = Array.Empty<string>();
    public double GainDb { get; set; }
    public double LoudnessTargetLufs { get; set; } = -20;
    public AudioVariationMode Variation { get; set; } = AudioVariationMode.RoundRobinNoRepeat;
    public int CooldownMs { get; set; }
    public int MaxInstances { get; set; } = 1;
    public bool Loop { get; set; }
    public AudioPitchDef Pitch { get; set; } = new();
    public AudioDuckDef? Duck { get; set; }
    public bool Wired { get; set; }
    public string Trigger { get; set; } = "";
    public string ProductionBrief { get; set; } = "";
    public string MixNotes { get; set; } = "";
}

public sealed class AudioStemDef
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public double GainDb { get; set; }
}

public sealed class AudioMusicStateDef
{
    public Dictionary<string, double> Gains { get; set; } = new(StringComparer.Ordinal);
    public bool Adaptive { get; set; }
}

public sealed class AudioAdaptiveBandDef
{
    public double OnAt { get; set; }
    public double FullAt { get; set; }
    public double MinGainDb { get; set; } = -60;
    public double MaxGainDb { get; set; }
}

public sealed class AudioAdaptiveDef
{
    /// <summary>Flow is expressed in hundredths (100 = 1.00x, 250 = 2.50x).</summary>
    public AudioAdaptiveBandDef Flow { get; set; } = new();
    public AudioAdaptiveBandDef Heat { get; set; } = new();
}

public sealed class AudioMusicDef
{
    public int Bpm { get; set; } = 78;
    public int Bars { get; set; } = 4;
    public double LoopSeconds { get; set; }
    public double DriftCorrectionSeconds { get; set; } = 0.025;
    public List<AudioStemDef> Stems { get; set; } = new();
    public AudioStemDef Ambience { get; set; } = new();
    public Dictionary<string, AudioMusicStateDef> States { get; set; } = new(StringComparer.Ordinal);
    public AudioAdaptiveDef Adaptive { get; set; } = new();
    public int[] HeatThresholds { get; set; } = Array.Empty<int>();

    /// <summary>One bar in seconds; used to bar-quantise stem re-syncs.</summary>
    public double BarSeconds => Bpm <= 0 ? 0.25 : 240.0 / Bpm;
}

public sealed class AudioFadeDef
{
    public double Bus { get; set; } = 0.18;
    public double Stem { get; set; } = 1.2;
    public double Screen { get; set; } = 0.28;
}

public sealed class AudioEventTable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public string Version { get; set; } = "";
    public int MixRateHz { get; set; } = 44100;
    public int VoicePoolSize { get; set; } = 16;
    public int MaxOneShots { get; set; } = 12;
    public double MasterLimiterCeilingDb { get; set; } = -1;
    public double MasterTargetLufs { get; set; } = -18;
    public double MasterTruePeakDbTp { get; set; } = -1;
    public List<AudioBusDef> Buses { get; set; } = new();
    public AudioDuckDefaultsDef DuckDefaults { get; set; } = new();
    public double AdAttenuationDb { get; set; } = -30;
    public double PauseAttenuationDb { get; set; } = -8;
    public double BackgroundAttenuationDb { get; set; } = -80;
    public AudioFadeDef FadeSeconds { get; set; } = new();
    public AudioMusicDef Music { get; set; } = new();
    public List<AudioEventDef> Events { get; set; } = new();

    [JsonIgnore]
    public Dictionary<string, AudioEventDef> ById { get; private set; } = new(StringComparer.Ordinal);

    public static AudioEventTable Load(string path) => FromJson(File.ReadAllText(path));

    public static AudioEventTable FromJson(string json)
    {
        var table = JsonSerializer.Deserialize<AudioEventTable>(json, Options)
                    ?? throw new InvalidDataException("audio_events.json deserialised to null.");
        table.RebuildIndex();
        return table;
    }

    public void RebuildIndex()
    {
        ById = new Dictionary<string, AudioEventDef>(StringComparer.Ordinal);
        foreach (AudioEventDef e in Events) ById[e.Id] = e;
    }

    public AudioEventDef? Find(string id) => ById.TryGetValue(id, out var e) ? e : null;

    /// <summary>All res:// stream paths referenced by events, music stems and ambience.</summary>
    public IEnumerable<string> AllStreamPaths()
    {
        foreach (AudioEventDef e in Events)
            foreach (string s in e.Streams) yield return s;
        foreach (AudioStemDef stem in Music.Stems) yield return stem.Path;
        if (Music.Ambience is not null && Music.Ambience.Path.Length > 0)
            yield return Music.Ambience.Path;
    }

    /// <summary>
    /// Structural validation. Runs at boot (fail loudly, like ConfigService) and
    /// in CoreTests over the shipped data file. A malformed catalogue must never
    /// degrade silently into a half-wired soundscape.
    /// </summary>
    public bool Validate(out List<string> errors)
    {
        errors = new List<string>();
        RebuildIndex();

        if (Version.Length == 0) errors.Add("missing version");
        if (MixRateHz is not (44100 or 48000)) errors.Add($"mixRateHz {MixRateHz} is not 44100/48000");
        if (VoicePoolSize < 4) errors.Add("voicePoolSize < 4");
        if (MaxOneShots < 1 || MaxOneShots > VoicePoolSize)
            errors.Add("maxOneShots must be within 1..voicePoolSize");
        if (MasterLimiterCeilingDb > 0) errors.Add("master limiter ceiling must be <= 0 dB");
        if (MasterTargetLufs > -10) errors.Add("masterTargetLufs is suspiciously hot");
        if (AdAttenuationDb >= 0 || PauseAttenuationDb >= 0 || BackgroundAttenuationDb >= 0)
            errors.Add("attenuation values must be negative");

        var busNames = new HashSet<string>(StringComparer.Ordinal) { "Master" };
        foreach (AudioBusDef bus in Buses)
        {
            if (bus.Name.Length == 0 || bus.Name == "Master" || !busNames.Add(bus.Name))
                errors.Add($"invalid/duplicate bus name '{bus.Name}'");
            if (bus.VolumeDb > 0) errors.Add($"bus {bus.Name} has positive trim {bus.VolumeDb}");
        }
        foreach (AudioBusDef bus in Buses)
            if (!busNames.Contains(bus.Send))
                errors.Add($"bus {bus.Name} sends to unknown bus '{bus.Send}'");
        if (!busNames.Contains(DuckDefaults.Bus))
            errors.Add($"duckDefaults.bus '{DuckDefaults.Bus}' does not exist");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (AudioEventDef e in Events)
        {
            string tag = $"event {e.Id}";
            if (e.Id.Length == 0) errors.Add("event with empty id");
            else if (!ids.Add(e.Id)) errors.Add($"duplicate event id '{e.Id}'");
            if (!busNames.Contains(e.Bus)) errors.Add($"{tag}: unknown bus '{e.Bus}'");
            if (e.Streams.Length == 0) errors.Add($"{tag}: no streams");
            if (e.Priority is < 0 or > 3) errors.Add($"{tag}: priority out of 0..3");
            if (e.MaxInstances < 1 || e.MaxInstances > VoicePoolSize)
                errors.Add($"{tag}: maxInstances out of 1..voicePoolSize");
            if (e.CooldownMs < 0) errors.Add($"{tag}: negative cooldown");
            if (e.LoudnessTargetLufs is < -45 or > -10)
                errors.Add($"{tag}: loudness target outside -45..-10 LUFS");
            if (e.ProductionBrief.Length == 0) errors.Add($"{tag}: missing productionBrief");
            if (e.Loop && (e.Streams.Length != 1 || e.MaxInstances != 1))
                errors.Add($"{tag}: a looping event needs exactly 1 stream and maxInstances 1");
            foreach (string s in e.Streams)
            {
                if (!s.StartsWith("res://", StringComparison.Ordinal))
                    errors.Add($"{tag}: stream '{s}' is not a res:// path");
                paths.Add(s);
            }
            if (e.Streams.Length != e.Streams.Distinct(StringComparer.Ordinal).Count())
                errors.Add($"{tag}: duplicate stream path");
            AudioPitchDef p = e.Pitch;
            if (p.Mode == AudioPitchMode.Ladder &&
                (p.Semitones.Length == 0 || p.MagnitudeDivisor <= 0))
                errors.Add($"{tag}: ladder pitch needs semitones[] and a positive divisor");
            if (p.Semitones.Any(v => v < -24 || v > 24))
                errors.Add($"{tag}: ladder semitone outside -24..24");
            if (p.JitterCents < 0 || p.JitterCents > 50)
                errors.Add($"{tag}: jitterCents outside 0..50");
            if (e.Duck is not null)
            {
                if (!busNames.Contains(e.Duck.Bus)) errors.Add($"{tag}: duck bus unknown");
                if (e.Duck.DepthDb <= 0 || e.Duck.DepthDb > 24)
                    errors.Add($"{tag}: duck depth outside 0..24 dB");
                if (e.Duck.AttackMs < 0 || e.Duck.HoldMs < 0 || e.Duck.ReleaseMs < 0)
                    errors.Add($"{tag}: negative duck timing");
            }
        }

        if (Music.Stems.Count == 0) errors.Add("music has no stems");
        var stemNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (AudioStemDef stem in Music.Stems)
        {
            if (!stemNames.Add(stem.Name)) errors.Add($"duplicate stem '{stem.Name}'");
            if (!stem.Path.StartsWith("res://", StringComparison.Ordinal))
                errors.Add($"stem {stem.Name}: not a res:// path");
            paths.Add(stem.Path);
        }
        if (Music.Ambience is null || !Music.Ambience.Path.StartsWith("res://", StringComparison.Ordinal))
            errors.Add("ambience path missing");
        else paths.Add(Music.Ambience.Path);

        foreach (string required in new[] { "Home", "Forge", "RuneDraft", "RunEnd" })
            if (!Music.States.ContainsKey(required)) errors.Add($"missing music state '{required}'");
        foreach (var (state, def) in Music.States)
        {
            foreach (string stem in stemNames)
                if (!def.Gains.ContainsKey(stem)) errors.Add($"state {state} has no gain for stem {stem}");
            foreach (string stem in def.Gains.Keys)
                if (!stemNames.Contains(stem)) errors.Add($"state {state} references unknown stem {stem}");
        }
        if (Music.Adaptive.Flow.OnAt >= Music.Adaptive.Flow.FullAt)
            errors.Add("flow adaptive band is inverted");
        if (Music.Adaptive.Heat.OnAt >= Music.Adaptive.Heat.FullAt)
            errors.Add("heat adaptive band is inverted");
        if (Music.LoopSeconds <= 0) errors.Add("music.loopSeconds must be positive");
        if (Music.BarSeconds <= 0) errors.Add("music.bpm is invalid");
        if (Music.HeatThresholds.Length == 0) errors.Add("music.heatThresholds is empty");

        return errors.Count == 0;
    }

    /// <summary>
    /// Asset existence check used by CI. godotRoot is the folder containing
    /// project.godot (res:// resolves relative to it).
    /// </summary>
    public bool ValidateAssets(string godotRoot, out List<string> errors)
    {
        errors = new List<string>();
        foreach (string res in AllStreamPaths().Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
        {
            string relative = res.Substring("res://".Length).Replace('/', Path.DirectorySeparatorChar);
            string full = Path.Combine(godotRoot, relative);
            if (!File.Exists(full)) errors.Add($"missing asset: {res} -> {full}");
        }
        return errors.Count == 0;
    }

    /// <summary>Maps a normalised 0..1 intensity onto an adaptive stem gain in dB.</summary>
    public static double AdaptiveGain(AudioAdaptiveBandDef band, double value)
    {
        if (value <= band.OnAt) return band.MinGainDb;
        if (value >= band.FullAt) return band.MaxGainDb;
        double t = (value - band.OnAt) / (band.FullAt - band.OnAt);
        // Equal-power-ish taper: rise quickly at first, then settle.
        double shaped = Math.Sqrt(t);
        return band.MinGainDb + (band.MaxGainDb - band.MinGainDb) * shaped;
    }
}

/// <summary>
/// Stateless-per-event variation selection. RoundRobinNoRepeat guarantees no
/// immediate repeat while still cycling the whole palette, which matters for
/// tile.pick (hundreds of triggers per session). Kept pure so CoreTests can
/// assert the anti-repetition property.
/// </summary>
public sealed class AudioVariationPicker
{
    private int _cursor;
    private int _last = -1;
    private readonly Random _rng;

    public AudioVariationPicker(int seed) => _rng = new Random(seed);

    public int Next(int count, AudioVariationMode mode, int magnitude)
    {
        if (count <= 0) return 0;
        if (count == 1) return 0;
        if (mode == AudioVariationMode.ByMagnitude)
            return Math.Clamp(magnitude, 0, count - 1);
        if (mode == AudioVariationMode.Random)
        {
            int r = _rng.Next(count);
            if (r == _last) r = (r + 1) % count;
            _last = r;
            return r;
        }
        int index = (_cursor + (_rng.Next(2) == 0 ? 0 : 1)) % count;
        if (index == _last) index = (index + 1) % count;
        _cursor = (index + 1) % count;
        _last = index;
        return index;
    }
}
