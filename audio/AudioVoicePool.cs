using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.Audio;

/// <summary>
/// Fixed pool of AudioStreamPlayer voices for all one-shots. A 2D word game has
/// no spatialisation, so explicit pooled players are cheaper and more predictable
/// than one player per emitter — and they give us real priority-based voice
/// stealing, which Godot's max_polyphony does not.
///
/// Zero per-playback allocations: streams are cached, variations are picked from
/// a pure-C# picker, and the voice metadata array is preallocated (GDD 11.5's
/// "no per-frame allocations" rule applies to audio too).
/// </summary>
public sealed partial class AudioVoicePool : Node
{
    private sealed class Voice
    {
        public string EventId = "";
        public int Priority;
        public ulong StartedMs;
    }

    private readonly Dictionary<string, AudioStream> _streamCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioStream[]> _streamsByEvent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioVariationPicker> _pickers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _lastPlayMs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    private AudioEventTable _table = new();
    private AudioStreamPlayer[] _players = Array.Empty<AudioStreamPlayer>();
    private Voice?[] _voices = Array.Empty<Voice?>();
    private Random _rng = new(20261112);

    /// <summary>Diagnostics: catalogued events that have at least one loaded stream.</summary>
    public int LoadedEventCount
    {
        get
        {
            int n = 0;
            foreach (KeyValuePair<string, AudioStream[]> pair in _streamsByEvent)
                if (pair.Value.Length > 0) n++;
            return n;
        }
    }

    /// <summary>Diagnostics: stream paths that failed to load.</summary>
    public int MissingStreamCount => _warned.Count;

    public void Initialize(AudioEventTable table)
    {
        _table = table;
        int size = table.VoicePoolSize;
        _players = new AudioStreamPlayer[size];
        _voices = new Voice?[size];

        // Preload every one-shot. Short PCM/OGG SFX are tiny; loading on first
        // tap would put a disk read in the input-to-sound latency path.
        foreach (AudioEventDef def in table.Events)
        {
            if (def.Loop) continue;
            var loaded = new List<AudioStream>(def.Streams.Length);
            foreach (string path in def.Streams)
            {
                if (!_streamCache.TryGetValue(path, out AudioStream? stream))
                {
                    stream = GD.Load<AudioStream>(path);
                    if (stream is null)
                    {
                        if (_warned.Add(path))
                        {
                            GD.PushWarning($"[Audio] missing stream {path}");
                            AnalyticsService.Instance.Record("audio_missing_stream", path);
                        }
                        continue;
                    }
                    _streamCache[path] = stream;
                }
                loaded.Add(stream);
            }
            _streamsByEvent[def.Id] = loaded.ToArray();
            _pickers[def.Id] = new AudioVariationPicker(StableHash(def.Id));
        }

        for (int i = 0; i < size; i++)
        {
            var player = new AudioStreamPlayer { Name = $"Voice{i:00}" };
            // Voices are children of an autoload with ProcessMode.Always, so a
            // paused tree never cuts a cue off mid-tail.
            player.ProcessMode = ProcessModeEnum.Always;
            AddChild(player);
            int slot = i;
            player.Finished += () => Release(slot);
            _players[i] = player;
            _voices[i] = null;
        }
    }

    public void Play(string eventId, int magnitude, bool reducedDensity)
    {
        AudioEventDef? def = _table.Find(eventId);
        if (def is null || def.Loop) return;
        if (!_streamsByEvent.TryGetValue(eventId, out AudioStream[]? streams) || streams.Length == 0)
            return;

        // Reduced Sound Density (accessibility): keep tactile feedback for tiles
        // and buttons, drop decorative low-priority layers entirely.
        if (reducedDensity && def.Priority == 0 && eventId is not ("tile.pick" or "ui.tap")) return;

        ulong now = Time.GetTicksMsec();
        ulong cooldown = (ulong)(def.CooldownMs * (reducedDensity ? 2 : 1));
        if (_lastPlayMs.TryGetValue(eventId, out ulong last) && now - last < cooldown) return;

        int active = 0;
        int activeThisEvent = 0;
        int oldestThisEvent = -1;
        ulong oldestStart = ulong.MaxValue;
        for (int i = 0; i < _voices.Length; i++)
        {
            Voice? v = _voices[i];
            if (v is null) continue;
            active++;
            if (v.EventId != eventId) continue;
            activeThisEvent++;
            if (v.StartedMs < oldestStart) { oldestStart = v.StartedMs; oldestThisEvent = i; }
        }

        int maxInstances = Math.Max(1, reducedDensity ? Math.Min(def.MaxInstances, 2) : def.MaxInstances);
        int slot = -1;

        // Per-event polyphony cap: recycle the oldest voice of the SAME event so
        // a fast drag can never build a wall of identical tile toks.
        if (activeThisEvent >= maxInstances && oldestThisEvent >= 0)
        {
            slot = oldestThisEvent;
            _players[slot].Stop();
            _voices[slot] = null;
        }

        if (slot < 0)
        {
            for (int i = 0; i < _players.Length; i++)
                if (!_players[i].Playing && _voices[i] is null) { slot = i; break; }
        }

        // Global one-shot ceiling. Hero cues (priority 3) may still steal; quiet
        // decoration is dropped so the mix never turns to mud.
        if (slot < 0 || active >= _table.MaxOneShots)
        {
            if (def.Priority < 2)
            {
                AnalyticsService.Instance.Record("audio_voice_starved", eventId);
                return;
            }
            int candidate = LowestPriorityVoice(def.Priority);
            if (candidate < 0)
            {
                AnalyticsService.Instance.Record("audio_voice_starved", eventId);
                return;
            }
            slot = candidate;
            _players[slot].Stop();
            _voices[slot] = null;
        }

        _lastPlayMs[eventId] = now;
        int variation = _pickers[eventId].Next(streams.Length, def.Variation, magnitude);

        double jitterUnit = reducedDensity ? 0.0 : _rng.NextDouble() * 2.0 - 1.0;
        float pitch = def.Pitch.PitchScale(magnitude, jitterUnit);
        float gainDb = (float)def.GainDb + (float)(jitterUnit * 0.5);

        AudioStreamPlayer player = _players[slot];
        player.Stream = streams[variation];
        player.Bus = new StringName(def.Bus);
        player.VolumeDb = gainDb;
        player.PitchScale = pitch;
        player.Play();

        _voices[slot] = new Voice { EventId = eventId, Priority = def.Priority, StartedMs = now };

        AudioDirector.Instance.RequestDuck(def.Duck);
        if (def.Priority == 3) AudioDirector.Instance.Haptic(18);
        AnalyticsService.Instance.Record("audio_play", eventId, def.Priority.ToString());
    }

    /// <summary>Stops every one-shot. Used when the app loses focus.</summary>
    public void StopAll()
    {
        for (int i = 0; i < _players.Length; i++)
        {
            if (_players[i].Playing) _players[i].Stop();
            _voices[i] = null;
        }
    }

    public override void _Process(double delta)
    {
        // Safety net: Stop() does not reliably emit Finished on every Godot
        // build, so also reap voices whose player is no longer playing.
        for (int i = 0; i < _players.Length; i++)
            if (_voices[i] is not null && !_players[i].Playing) Release(i);
    }

    private void Release(int slot)
    {
        if (slot >= 0 && slot < _voices.Length) _voices[slot] = null;
    }

    private int LowestPriorityVoice(int incomingPriority)
    {
        int best = -1;
        int bestPriority = int.MaxValue;
        ulong bestStart = ulong.MaxValue;
        for (int i = 0; i < _voices.Length; i++)
        {
            Voice? v = _voices[i];
            if (v is null || v.Priority > incomingPriority) continue;
            if (v.Priority < bestPriority ||
                (v.Priority == bestPriority && v.StartedMs < bestStart))
            {
                best = i; bestPriority = v.Priority; bestStart = v.StartedMs;
            }
        }
        return best;
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            int h = 17;
            foreach (char c in s) h = h * 31 + c;
            return h;
        }
    }
}
