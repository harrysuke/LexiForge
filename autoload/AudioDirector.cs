using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Audio;
using LexiForge.Core;
using FileAccess = Godot.FileAccess;

namespace LexiForge.Autoload;

/// <summary>
/// The ONLY Godot-facing owner of the soundscape (design/AUDIO_DESIGN.md).
///
/// Owns: bus layout, user volume settings, master fades, ducking envelopes,
/// application focus, ad playback attenuation and haptics. Playback mechanics
/// are delegated to AudioVoicePool (one-shots) and MusicDirector (loops).
///
/// Registered LAST in project.godot autoloads so SaveService/ConfigService/
/// EventBus/AdManager are all ready before _Ready() runs. Gameplay code never
/// touches this class directly except for lifecycle calls; AudioEventBridge
/// translates EventBus signals into Play() calls, which keeps audio decoupled.
///
/// AudioDirector OWNS the bus layout at runtime: do NOT also ship a
/// default_bus_layout.tres, or the editor layout and this one will fight.
/// </summary>
public sealed partial class AudioDirector : Node
{
    public static AudioDirector Instance { get; private set; } = null!;

    private const string CatalogPath = "res://data/audio_events.json";

    private readonly Dictionary<string, int> _busIndices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnedMissing = new(StringComparer.Ordinal);

    private AudioEventTable _table = new();
    private AudioVoicePool _pool = null!;
    private MusicDirector _music = null!;
    private AudioEventBridge _bridge = null!;

    // Settings (mirrored into SaveData, which owns persistence).
    private float _musicVolume = 0.85f;
    private float _sfxVolume = 1.0f;
    private float _ambienceVolume = 0.7f;
    private bool _muted;
    private bool _reducedSoundDensity;
    private bool _hapticsEnabled = true;

    // Fade state, all in dB relative to the catalog's bus trims.
    private float _masterExtraDb;          // 0 normally; ad / pause / background
    private float _masterExtraTargetDb;
    private float _duckDb;                 // negative, applied to Music only
    private float _duckTargetDb;
    private float _duckAttackDbPerSec = 40f;
    private float _duckReleaseDbPerSec = 20f;
    private double _duckHoldSeconds;
    private bool _appSuspended;
    private bool _adPlaying;
    private bool _paused;
    private double _adWatchdogSeconds;

    /// <summary>Upper bound on an ad duck. Interstitials have no close callback
    /// on every mediation SDK, so this is the only thing that guarantees the
    /// game is not left 30 dB quiet for the rest of the session.</summary>
    private const double AdWatchdogLimitSeconds = 30.0;

    public AudioEventTable Table => _table;
    public float MusicVolume => _musicVolume;
    public float SfxVolume => _sfxVolume;
    public float AmbienceVolume => _ambienceVolume;
    public bool Muted => _muted;
    public bool ReducedSoundDensity => _reducedSoundDensity;
    public bool HapticsEnabled => _hapticsEnabled;

    public override void _Ready()
    {
        Instance = this;
        // Music, settings fades and focus handling must keep running while
        // GetTree().Paused is true (the pause overlay stops everything else).
        ProcessMode = ProcessModeEnum.Always;

        _table = LoadCatalog();
        BuildBuses();
        LoadSettings();

        _pool = new AudioVoicePool();
        AddChild(_pool);
        _pool.Initialize(_table);

        _music = new MusicDirector();
        AddChild(_music);
        _music.Initialize(_table);
        _music.Start();

        _bridge = new AudioEventBridge();
        AddChild(_bridge);

        ApplyBusVolumes();
        GD.Print($"[Audio] {_table.Version} | events={_table.Events.Count} " +
                 $"buses={_table.Buses.Count} voices={_table.VoicePoolSize} " +
                 $"stems={_table.Music.Stems.Count}");
    }

    private AudioEventTable LoadCatalog()
    {
        if (!FileAccess.FileExists(CatalogPath))
            throw new InvalidOperationException(
                $"[Audio] {CatalogPath} is missing. Run godot/tools/AudioGreybox/generate_greybox.py.");

        using var file = FileAccess.Open(CatalogPath, FileAccess.ModeFlags.Read);
        AudioEventTable table = AudioEventTable.FromJson(file.GetAsText());
        if (!table.Validate(out List<string> errors))
            throw new InvalidOperationException("[Audio] invalid audio_events.json: " + string.Join(" | ", errors));
        return table;
    }

    /// <summary>
    /// Idempotent bus creation. If a bus already exists (e.g. an editor layout)
    /// we reuse it and only correct its routing/trim; otherwise we append it.
    /// All sends are resolved in a SECOND pass so order never matters.
    /// </summary>
    private void BuildBuses()
    {
        foreach (AudioBusDef bus in _table.Buses)
        {
            int index = AudioServer.GetBusIndex(new StringName(bus.Name));
            if (index < 0)
            {
                index = AudioServer.GetBusCount();
                AudioServer.AddBus(-1);
                AudioServer.SetBusName(index, new StringName(bus.Name));
            }
            _busIndices[bus.Name] = index;
        }
        foreach (AudioBusDef bus in _table.Buses)
            AudioServer.SetBusSend(_busIndices[bus.Name], new StringName(bus.Send));

        // Single safety net against stacked one-shots clipping a phone speaker.
        // This is a brick-wall safety net, NOT a substitute for the LUFS mix pass.
        // Godot 4.7 deprecates AudioEffectLimiter; the hard limiter predicts
        // peaks instead of soft-clipping them, which is what a master bus wants.
        var limiter = new AudioEffectHardLimiter
        {
            CeilingDb = (float)_table.MasterLimiterCeilingDb,
            Release = 0.2f
        };
        AudioServer.AddBusEffect(0, limiter);
    }

    private void LoadSettings()
    {
        SaveData save = SaveService.Instance.Data;
        _musicVolume = Clamp01(save.MusicVolume);
        _sfxVolume = Clamp01(save.SfxVolume);
        _ambienceVolume = Clamp01(save.AmbienceVolume);
        _muted = save.AudioMuted;
        _reducedSoundDensity = save.ReducedSoundDensity;
        _hapticsEnabled = save.HapticsEnabled;
    }

    // ------------------------------------------------------------- public API
    /// <summary>Play a catalogued one-shot. magnitude is the semantic intensity
    /// used by ladder pitch (letter index) and Flow motifs (flow hundredths).</summary>
    public void Play(string eventId, int magnitude = 0)
    {
        if (_pool is null) return;
        if (_table.Find(eventId) is null)
        {
            if (_warnedMissing.Add(eventId))
            {
                GD.PushWarning($"[Audio] unknown event '{eventId}' — check audio_events.json.");
                AnalyticsService.Instance.Record("audio_missing_event", eventId);
            }
            return;
        }
        _pool.Play(eventId, magnitude, _reducedSoundDensity);
    }

    /// <summary>Hero cue ducking. Multiple ducks keep the deepest one.</summary>
    public void RequestDuck(AudioDuckDef? duck)
    {
        if (duck is null || duck.DepthDb <= 0) return;
        float target = -(float)duck.DepthDb;
        if (target < _duckTargetDb) _duckTargetDb = target;
        _duckAttackDbPerSec = Math.Max(1f, (float)duck.DepthDb / Math.Max(0.01f, duck.AttackMs / 1000f));
        _duckReleaseDbPerSec = Math.Max(1f, (float)duck.DepthDb / Math.Max(0.01f, duck.ReleaseMs / 1000f));
        _duckHoldSeconds = Math.Max(_duckHoldSeconds, duck.HoldMs / 1000.0);
    }

    public void SetMusicState(string state) => _music?.SetState(state);
    public void SetMusicIntensity(int flowHundredths, int heat) => _music?.SetIntensity(flowHundredths, heat);

    /// <summary>In-game pause overlay: -8 dB duck, never a hard mute — the
    /// player should still hear that the game is alive and waiting.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        RefreshMasterTarget();
    }

    /// <summary>
    /// Rewarded/interstitial/app-open playback. Ad creative owns the audio
    /// channel; game audio drops 30 dB rather than fighting it. Called by
    /// AdManager at show start/end and by AudioEventBridge on RewardGranted.
    /// Idempotent so the two callers can both end the same impression without
    /// double-ramping or double-counting analytics.
    /// </summary>
    public void SetAdPlayback(bool active)
    {
        if (_adPlaying == active) return;
        _adPlaying = active;
        _adWatchdogSeconds = active ? AdWatchdogLimitSeconds : 0.0;
        RefreshMasterTarget();
        AnalyticsService.Instance.Record("audio_ad_duck", active ? "start" : "end");
    }

    /// <summary>
    /// Live preview used while a settings slider is being dragged: applies the
    /// mix immediately but does NOT touch the save file. Persisting on every
    /// ValueChanged would write the save once per pixel of drag.
    /// </summary>
    public void PreviewSettings(float music, float sfx, float ambience, bool muted,
                                bool reducedDensity, bool haptics)
    {
        _musicVolume = Clamp01(music);
        _sfxVolume = Clamp01(sfx);
        _ambienceVolume = Clamp01(ambience);
        _muted = muted;
        _reducedSoundDensity = reducedDensity;
        _hapticsEnabled = haptics;
        ApplyBusVolumes();
    }

    /// <summary>UI settings. Persists through SaveData's atomic write path.</summary>
    public void ApplySettings(float music, float sfx, float ambience, bool muted,
                              bool reducedDensity, bool haptics)
    {
        PreviewSettings(music, sfx, ambience, muted, reducedDensity, haptics);

        SaveData save = SaveService.Instance.Data;
        save.MusicVolume = _musicVolume;
        save.SfxVolume = _sfxVolume;
        save.AmbienceVolume = _ambienceVolume;
        save.AudioMuted = _muted;
        save.ReducedSoundDensity = _reducedSoundDensity;
        save.HapticsEnabled = _hapticsEnabled;
        SaveService.Instance.MarkDirty();
        SaveService.Instance.Flush();

        AnalyticsService.Instance.Record("audio_settings_changed",
            _musicVolume.ToString("F2"), _sfxVolume.ToString("F2"), _muted.ToString());
    }

    /// <summary>Optional tactile reinforcement. Never a substitute for audio:
    /// every cue has a visual counterpart (GDD player-first charter).</summary>
    public void Haptic(int milliseconds)
    {
        if (!_hapticsEnabled || _appSuspended || OS.GetName() != "Android") return;
        Input.VibrateHandheld(Math.Clamp(milliseconds, 5, 40));
    }

    // -------------------------------------------------------------- internals
    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Watchdog: an interstitial that never reports its close must not leave
        // the game 30 dB quiet until the app is restarted.
        if (_adPlaying && _adWatchdogSeconds > 0)
        {
            _adWatchdogSeconds -= dt;
            if (_adWatchdogSeconds <= 0) SetAdPlayback(false);
        }

        if (_duckHoldSeconds > 0)
        {
            _duckHoldSeconds -= dt;
            if (_duckHoldSeconds <= 0) _duckTargetDb = 0f;
        }
        float duckRate = _duckTargetDb < _duckDb ? _duckAttackDbPerSec : _duckReleaseDbPerSec;
        _duckDb = MoveTowards(_duckDb, _duckTargetDb, duckRate * dt);

        float masterRate = 80f / Math.Max(0.02f, (float)_table.FadeSeconds.Bus);
        _masterExtraDb = MoveTowards(_masterExtraDb, _masterExtraTargetDb, masterRate * dt);
        ApplyBusVolumes();
    }

    private void RefreshMasterTarget()
    {
        _masterExtraTargetDb = _appSuspended ? (float)_table.BackgroundAttenuationDb
                             : _adPlaying ? (float)_table.AdAttenuationDb
                             : _paused ? (float)_table.PauseAttenuationDb
                             : 0f;
    }

    private void ApplyBusVolumes()
    {
        AudioServer.SetBusVolumeDb(0, _masterExtraDb);
        AudioServer.SetBusMute(0, _muted || _appSuspended);

        foreach (AudioBusDef bus in _table.Buses)
        {
            if (!_busIndices.TryGetValue(bus.Name, out int index)) continue;
            float user = bus.Name switch
            {
                "Music" => _musicVolume,
                "AMB" => _reducedSoundDensity ? 0f : _ambienceVolume,
                _ => _sfxVolume                       // SFX + UI share one slider
            };
            float extra = bus.Name == "Music" ? _duckDb : 0f;
            AudioServer.SetBusVolumeDb(index, (float)bus.VolumeDb + LinearToDb(user) + extra);
            AudioServer.SetBusMute(index, bus.Name == "AMB" && _reducedSoundDensity);
        }
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        switch ((long)what)
        {
            case Node.NotificationApplicationPaused:
            case Node.NotificationApplicationFocusOut:
                // Audio focus loss is an OS-level event, not a user setting.
                // Do not persist it and do not resume audio until focus returns.
                _appSuspended = true;
                // Cut in-flight one-shots: a tile tok must not keep ringing over
                // another app's audio. Stems stay loaded and resume in phase.
                _pool?.StopAll();
                RefreshMasterTarget();
                ApplyBusVolumes();
                AnalyticsService.Instance.Record("audio_focus_loss");
                break;
            case Node.NotificationApplicationResumed:
                _appSuspended = false;
                // A full-screen ad activity returns focus here. Release the ad
                // duck even if the SDK never reported a close; a rewarded ad
                // still on screen would not have resumed yet.
                if (_adPlaying) SetAdPlayback(false);
                RefreshMasterTarget();
                ApplyBusVolumes();
                break;
            case Node.NotificationWMCloseRequest:
                SaveService.Instance.Flush();
                break;
        }
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>System.Math, not Godot.Mathf — identical on every target and testable in tools.</summary>
    private static float LinearToDb(float linear)
        => (float)(20.0 * Math.Log10(Math.Max(linear, 0.0001)));

    private static float MoveTowards(float current, float target, float maxDelta)
        => Math.Abs(target - current) <= maxDelta ? target : current + Math.Sign(target - current) * maxDelta;
}
