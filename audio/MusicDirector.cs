using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Autoload;
using LexiForge.Core;

namespace LexiForge.Audio;

/// <summary>
/// Adaptive, vertically-layered score (design/AUDIO_DESIGN.md section 5).
///
/// Four bar-aligned stems (bed / flow / heat / draft) all loop at the same
/// length and start on the same frame. Gains — not start/stop — express game
/// state, so a transition can never click or drop a beat. Flow and Heat drive
/// the two "adaptive" stems through smoothed dB targets; every other state uses
/// fixed gains from the catalog.
///
/// Godot's AudioStreamPlayers are not sample-locked to each other, so a slow
/// drift correction re-aligns any stem that slips >25 ms, and only at a bar
/// boundary (never mid-phrase). AudioServer.GetTimeSinceLastMix() compensates
/// for the mix-buffer offset when re-seating a player.
/// </summary>
public sealed partial class MusicDirector : Node
{
    private sealed class Stem
    {
        public string Name = "";
        public AudioStreamPlayer Player = null!;
        public double TargetDb;
        public double CurrentDb;
    }

    private readonly List<Stem> _stems = new();
    private AudioEventTable _table = new();
    private AudioStreamPlayer? _ambience;
    private string _state = "Home";
    private int _flowHundredths = 100;
    private int _heat;
    private bool _started;

    public void Initialize(AudioEventTable table)
    {
        _table = table;

        foreach (AudioStemDef def in table.Music.Stems)
        {
            AudioStream? stream = GD.Load<AudioStream>(def.Path);
            if (stream is null)
            {
                GD.PushWarning($"[Audio] missing music stem {def.Path}");
                AnalyticsService.Instance.Record("audio_missing_stream", def.Path);
                continue;
            }
            EnableLoop(stream);
            var player = new AudioStreamPlayer
            {
                Name = "Stem_" + def.Name,
                Stream = stream,
                Bus = new StringName("Music"),
                VolumeDb = (float)def.GainDb,
                ProcessMode = ProcessModeEnum.Always
            };
            AddChild(player);
            player.Finished += () => { if (_started) player.Play(0f); };
            _stems.Add(new Stem { Name = def.Name, Player = player, CurrentDb = def.GainDb, TargetDb = def.GainDb });
        }

        AudioStemDef amb = table.Music.Ambience;
        AudioStream? ambStream = GD.Load<AudioStream>(amb.Path);
        if (ambStream is not null)
        {
            EnableLoop(ambStream);
            _ambience = new AudioStreamPlayer
            {
                Name = "Ambience_ForgeRoom",
                Stream = ambStream,
                Bus = new StringName("AMB"),
                VolumeDb = (float)amb.GainDb,
                ProcessMode = ProcessModeEnum.Always
            };
            AddChild(_ambience);
            _ambience.Finished += () => { if (_started) _ambience.Play(0f); };
        }

        RecomputeTargets();
    }

    /// <summary>Start every stem on the same frame. Call once at boot.</summary>
    public void Start()
    {
        if (_started) return;
        _started = true;
        foreach (Stem stem in _stems) stem.Player.Play(0f);
        _ambience?.Play(0f);
    }

    public void SetState(string state)
    {
        string next = _table.Music.States.ContainsKey(state) ? state : "Home";
        if (next == _state) return;
        _state = next;
        RecomputeTargets();
        AnalyticsService.Instance.Record("audio_music_state", next);
    }

    public void SetIntensity(int flowHundredths, int heat)
    {
        _flowHundredths = Math.Clamp(flowHundredths, 100, 250);
        _heat = Math.Clamp(heat, 0, 100);
        RecomputeTargets();
    }

    private void RecomputeTargets()
    {
        if (!_table.Music.States.TryGetValue(_state, out AudioMusicStateDef? state)) return;
        foreach (Stem stem in _stems)
        {
            double target = state.Gains.TryGetValue(stem.Name, out double gain) ? gain : -60.0;
            if (state.Adaptive && stem.Name == "flow")
                target = Math.Max(target, AudioEventTable.AdaptiveGain(
                    _table.Music.Adaptive.Flow, _flowHundredths));
            if (state.Adaptive && stem.Name == "heat")
                target = Math.Max(target, AudioEventTable.AdaptiveGain(
                    _table.Music.Adaptive.Heat, _heat));
            stem.TargetDb = target;
        }
    }

    public override void _Process(double delta)
    {
        if (!_started || _stems.Count == 0) return;
        float dt = (float)delta;

        // Exponential dB glide: perceptually even, and cheap on low-end Android.
        double tau = Math.Max(0.05, _table.FadeSeconds.Stem / 3.0);
        double k = 1.0 - Math.Exp(-dt / tau);
        foreach (Stem stem in _stems)
        {
            stem.CurrentDb += (stem.TargetDb - stem.CurrentDb) * k;
            stem.Player.VolumeDb = (float)stem.CurrentDb;
        }

        CorrectDrift();
    }

    private void CorrectDrift()
    {
        double bar = _table.Music.BarSeconds;
        double loop = _table.Music.LoopSeconds;
        double anchor = _stems[0].Player.GetPlaybackPosition();
        if (bar <= 0 || loop <= 0) return;
        // Only re-seat at a bar line; a mid-bar jump would be audible.
        if (anchor % bar > 0.03) return;

        double offset = AudioServer.GetTimeSinceLastMix();
        for (int i = 1; i < _stems.Count; i++)
        {
            Stem stem = _stems[i];
            double pos = stem.Player.GetPlaybackPosition();
            double diff = Math.Abs(pos - anchor);
            diff = Math.Min(diff, Math.Abs(loop - diff));
            if (diff <= _table.Music.DriftCorrectionSeconds) continue;
            double target = (anchor + offset) % loop;
            stem.Player.Play((float)target);
            AnalyticsService.Instance.Record("audio_stem_resync", stem.Name, diff.ToString("F3"));
        }
    }

    /// <summary>
    /// Runtime loop flags so no per-file .import settings are needed. WAV uses
    /// LoopEnd=0 (= whole stream); final OGG stems should set LOOPSTART/LOOPEND
    /// Vorbis comments or LoopOffset in the import dock instead.
    /// </summary>
    private static void EnableLoop(AudioStream stream)
    {
        if (stream is AudioStreamWav wav)
        {
            wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            wav.LoopBegin = 0;
            wav.LoopEnd = 0;
        }
        else if (stream is AudioStreamOggVorbis ogg)
        {
            ogg.Loop = true;
            ogg.LoopOffset = 0f;
        }
    }
}
