using System;
using System.Collections.Generic;
using Godot;
using LexiForge.Core;
using LexiForge.UI;

namespace LexiForge.Autoload;

/// <summary>
/// Screen transitions. The Forge scene is instantiated ONCE and reused through
/// IForgeScreen.ResetFor — GDD 11.5 explicitly forbids reloading the puzzle
/// scene between forges (it re-creates tile nodes, GCs the word buffers and
/// causes a visible hitch on low-end Android).
///
/// The host is Boot's root Control, attached by Boot._Ready(). Autoloads are
/// added to the SceneTree before the main scene, so letting the router create
/// its own root child would put every screen BEHIND Boot.
///
/// Boot owns the FIRST navigation: it subscribes to EventBus.DataReady and also
/// checks GameDataService.IsReady synchronously. The router must not navigate
/// from AttachHost, or that race would call a screen's OnEnter twice.
/// </summary>
public sealed partial class SceneRouter : Node
{
    public static SceneRouter Instance { get; private set; } = null!;

    private static readonly Dictionary<ScreenId, string> Paths = new()
    {
        [ScreenId.Home] = "res://ui/screens/Home.tscn",
        [ScreenId.Forge] = "res://ui/screens/Forge.tscn",
        [ScreenId.RuneDraft] = "res://ui/screens/RuneDraft.tscn",
        [ScreenId.RunEnd] = "res://ui/screens/RunEnd.tscn"
    };

    private readonly Dictionary<ScreenId, Node> _cache = new();
    private Node _host = null!;
    private Node? _current;
    private ScreenId _currentId;

    public ScreenId CurrentScreen => _currentId;
    public bool IsPaused { get; private set; }

    public override void _Ready()
    {
        Instance = this;
        ProcessMode = ProcessModeEnum.Always;
    }

    public void AttachHost(Node host) => _host = host;

    private Node GetOrCreate(ScreenId id)
    {
        if (_cache.TryGetValue(id, out Node? cached) && GodotObject.IsInstanceValid(cached)) return cached;

        var packed = GD.Load<PackedScene>(Paths[id]);
        if (packed is null) throw new InvalidOperationException($"Missing screen scene: {Paths[id]}");
        Node node = packed.Instantiate();
        node.Name = id.ToString();
        _cache[id] = node;
        return node;
    }

    public void GoTo(ScreenId id, object? payload = null)
    {
        if (_host is null) { GD.PushError("[Router] no host attached; Boot must call AttachHost()."); return; }

        Node next = GetOrCreate(id);
        if (!ReferenceEquals(_current, next))
        {
            if (_current is not null)
            {
                (_current as IScreen)?.OnExit();
                _host.RemoveChild(_current);
            }
            if (next.GetParent() is null) _host.AddChild(next);
            _current = next;
        }
        _currentId = id;
        (next as IScreen)?.OnEnter(payload);
        EventBus.Instance.EmitSignal(EventBus.SignalName.ScreenChanged, id.ToString());
        AnalyticsService.Instance.Record("screen", id.ToString());
    }

    /// <summary>Reuses the pooled Forge scene and resets it for the next puzzle.</summary>
    public void GoToForge(ForgeData forge, int runIndex, RuneEffects effects)
    {
        Node node = GetOrCreate(ScreenId.Forge);
        if (node is not IForgeScreen forgeScreen)
            throw new InvalidOperationException("Forge.tscn root must implement IForgeScreen.");
        GoTo(ScreenId.Forge);
        forgeScreen.ResetFor(forge, runIndex, effects);
    }

    public void GoToRuneDraft(object? payload = null) => GoTo(ScreenId.RuneDraft, payload);
    public void GoToRunEnd(object? payload = null) => GoTo(ScreenId.RunEnd, payload);
    public void GoHome() => GoTo(ScreenId.Home);

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        GetTree().Paused = paused;
        if (paused) SaveService.Instance.Flush();
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        // Godot's notification constants are long in the C# bindings.
        switch ((long)what)
        {
            case Node.NotificationWMGoBackRequest:
                // quit_on_go_back is false in project.godot. Back always opens
                // the in-game pause, never kills the app mid-forge.
                if (_current is IScreen screen) screen.OnBack();
                else SetPaused(!IsPaused);
                break;
            case Node.NotificationApplicationPaused:
            case Node.NotificationApplicationFocusOut:
                SaveService.Instance.Flush();
                AnalyticsService.Instance.Flush();
                break;
            case Node.NotificationApplicationResumed:
                if (SaveService.Instance.OnNewDay()) AdManager.Instance.OnNewDay();
                // AdManager clears InRun on focus-out; without re-asserting it a
                // resumed forge would become eligible for an interstitial, which
                // GDD 8.3 forbids during a run.
                AdManager.Instance.SetInRun(_currentId is ScreenId.Forge or ScreenId.RuneDraft);
                break;
        }
    }
}
