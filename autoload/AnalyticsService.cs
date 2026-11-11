using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace LexiForge.Autoload;

/// <summary>
/// Offline-first event buffer (GDD 9.2 / 11.5). P0 deliberately has NO network
/// sink: events are queued in memory and appended to a small user:// journal on
/// pause so nothing is lost when the app is killed. Wiring a backend is a P1
/// concern and must go through this class only, so gameplay never learns about
/// an HTTP client.
///
/// No LINQ and no allocations in _Process — nothing here runs per frame.
/// </summary>
public sealed partial class AnalyticsService : Node
{
    public static AnalyticsService Instance { get; private set; } = null!;

    private const int FlushThreshold = 32;
    private const int MaxBuffer = 512;
    private readonly List<string> _queue = new();
    private string _journalPath = "";

    public override void _Ready()
    {
        Instance = this;
        _journalPath = ProjectSettings.GlobalizePath("user://analytics.jsonl");
        ProcessMode = ProcessModeEnum.Always;
    }

    /// <summary>Fields are already-sanitised strings; never pass raw user text/PII.</summary>
    public void Record(string eventName, params string[] fields)
    {
        if (_queue.Count >= MaxBuffer) _queue.RemoveAt(0);
        long now = (long)Time.GetUnixTimeFromSystem();
        string payload = string.Join("|", fields).Replace("\n", " ", StringComparison.Ordinal)
                                                 .Replace("|", "/", StringComparison.Ordinal);
        _queue.Add($"{{\"t\":{now},\"e\":\"{eventName}\",\"p\":\"{payload}\"}}");
        if (_queue.Count >= FlushThreshold) Flush();
    }

    public void Flush()
    {
        if (_queue.Count == 0) return;
        try
        {
            string? dir = Path.GetDirectoryName(_journalPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var writer = new StreamWriter(_journalPath, true);
            foreach (string line in _queue) writer.WriteLine(line);
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[Analytics] journal write failed: {ex.Message}");
        }
        finally
        {
            _queue.Clear();
        }
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        long notification = what;
        if (notification is Node.NotificationApplicationPaused or Node.NotificationApplicationFocusOut
            or Node.NotificationWMCloseRequest)
            Flush();
    }

    public override void _ExitTree() => Flush();
}
