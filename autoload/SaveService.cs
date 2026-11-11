using System;
using System.IO;
using Godot;
using LexiForge.Core;

namespace LexiForge.Autoload;

/// <summary>
/// Owns the versioned user://save.json. SaveData (pure C#) owns the SHAPE and
/// migrations; this service owns the ATOMIC WRITE so no other system can half-
/// write a save. Migrations and daily rollover are therefore unit-testable in
/// CoreTests without a scene tree.
///
/// Atomic write = write to save.json.tmp, Flush(true), then File.Move(..., true)
/// which is a rename on both Android and desktop. A crash mid-write leaves the
/// previous save intact.
/// </summary>
public sealed partial class SaveService : Node
{
    public static SaveService Instance { get; private set; } = null!;

    private const string SaveUri = "user://save.json";
    private string _savePath = "";
    private bool _dirty;

    public SaveData Data { get; private set; } = new();

    public override void _Ready()
    {
        Instance = this;
        _savePath = ProjectSettings.GlobalizePath(SaveUri);

        try
        {
            if (File.Exists(_savePath))
            {
                Data = SaveData.FromJson(File.ReadAllText(_savePath));
                if (!Data.IsValid(out string reason))
                {
                    GD.PushWarning($"[Save] rejected invalid save ({reason}); starting fresh.");
                    Data = new SaveData();
                }
            }
        }
        catch (Exception ex)
        {
            // A corrupt save must never brick the install. Keep the bad file for
            // support diagnostics and start a clean session.
            GD.PushWarning($"[Save] unreadable save: {ex.Message}. Starting a fresh profile.");
            try { File.Move(_savePath, _savePath + ".corrupt", true); } catch { /* best effort */ }
            Data = new SaveData();
        }

        Data.ConfigVersion = ConfigService.Instance.ConfigVersion;
        Data.Sessions++;
        OnNewDay();
        MarkDirty();
        GD.Print($"[Save] schema={Data.SchemaVersion} coins={Data.Coins} marks={Data.Marks} forges={Data.LifetimeForges}");
    }

    /// <summary>Rolls UTC-daily ad caps, streak freezes and placement counters.</summary>
    public bool OnNewDay()
    {
        long now = (long)Time.GetUnixTimeFromSystem();
        if (!Data.RollDaily(now)) return false;
        MarkDirty();
        GD.Print($"[Save] daily counters rolled over to day {Data.AdDay.DayNumber}");
        return true;
    }

    public void MarkDirty() => _dirty = true;

    public void RecordForge(int forgeId, int stars)
    {
        Data.RecordForge(forgeId, stars);
        MarkDirty();
    }

    public void SetConsentResolved(bool nonPersonalized, int policyVersion)
    {
        Data.ConsentResolved = true;
        Data.ConsentStatus = nonPersonalized ? "non_personalized" : "personalized";
        Data.ConsentPolicyVersion = policyVersion;
        MarkDirty();
        Flush();
    }

    public void Flush()
    {
        if (!_dirty) return;
        try
        {
            string? dir = Path.GetDirectoryName(_savePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = _savePath + ".tmp";
            Data.LastSavedUtcUnix = (long)Time.GetUnixTimeFromSystem();
            using (var writer = new StreamWriter(tmp, false))
            {
                writer.Write(Data.ToJson());
                writer.Flush();
            }
            File.Move(tmp, _savePath, true);
            _dirty = false;
        }
        catch (Exception ex)
        {
            GD.PushError($"[Save] flush failed: {ex.Message}");
        }
    }

    public override void _Notification(int what)
    {
        base._Notification(what);
        // Godot's notification constants are long in the C# bindings.
        switch ((long)what)
        {
            case Node.NotificationApplicationPaused:
            case Node.NotificationApplicationFocusOut:
                Flush();
                break;
            case Node.NotificationApplicationResumed:
                OnNewDay();
                break;
            case Node.NotificationWMCloseRequest:
                Flush();
                break;
        }
    }

    public override void _ExitTree() => Flush();
}
