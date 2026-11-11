using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using LexiForge.Core;
using FileAccess = Godot.FileAccess;

namespace LexiForge.Autoload;

/// <summary>
/// Loads the shipped binary trie and the read-only forge catalogue once at boot.
///
/// Threading contract (GDD 11.5): Godot's FileAccess/DirAccess are NOT thread
/// safe, so the RAW BYTES/TEXT are gathered on the main thread and only the
/// CPU-bound parsing runs on a Task. The result is published back on the main
/// thread with CallDeferred — no Godot API is ever touched from the worker, and
/// no scene reload is needed when the catalogue is ready.
///
/// res:// payloads are inside the APK/PCK on device, which is why parsing goes
/// through WordTrie.Load(Stream) and ForgeCatalog.LoadPackFromJson(string)
/// rather than any System.IO path.
/// </summary>
public sealed partial class GameDataService : Node
{
    public static GameDataService Instance { get; private set; } = null!;

    private const string TriePath = "res://data/words_en.dat";
    private const string ForgesDir = "res://data/forges";

    private WordTrie? _parsedTrie;
    private List<ForgeData>? _parsedForges;
    private string? _parseError;

    public bool IsReady { get; private set; }
    public string ErrorMessage => _parseError ?? "";
    public WordTrie Dictionary => _parsedTrie ?? throw new InvalidOperationException("Game data not loaded yet.");
    public IReadOnlyList<ForgeData> Forges
        => _parsedForges ?? (IReadOnlyList<ForgeData>)Array.Empty<ForgeData>();

    public override void _Ready() => Instance = this;

    /// <summary>
    /// Boot calls this on startup and again from its explicit error state. The
    /// raw res:// bytes must be gathered on the MAIN thread (Godot's FileAccess
    /// is not thread safe), so Retry re-reads them instead of reusing a buffer.
    /// </summary>
    public void StartLoad()
    {
        IsReady = false;
        _parseError = null;
        _parsedTrie = null;
        _parsedForges = null;

        if (!FileAccess.FileExists(TriePath))
        {
            Fail($"{TriePath} missing — add data/words_en.dat to export_presets include_filter.");
            return;
        }

        byte[] trieBytes = FileAccess.GetFileAsBytes(TriePath);
        var packTexts = new List<string>();
        DirAccess? dir = DirAccess.Open(ForgesDir);
        if (dir is null)
        {
            Fail($"{ForgesDir} missing — no forge catalogue shipped.");
            return;
        }
        foreach (string name in dir.GetFiles())
        {
            if (!name.StartsWith("pack_", StringComparison.Ordinal) ||
                !name.EndsWith(".json", StringComparison.Ordinal)) continue;
            string path = ForgesDir + "/" + name;
            using var file = FileAccess.Open(path, FileAccess.ModeFlags.Read);
            if (file is null) continue;
            packTexts.Add(file.GetAsText());
        }
        packTexts.Sort(StringComparer.Ordinal);
        if (packTexts.Count == 0)
        {
            Fail("No pack_XX.json files found under res://data/forges.");
            return;
        }

        _ = Task.Run(() => Parse(trieBytes, packTexts));
    }

    private void Parse(byte[] trieBytes, List<string> packTexts)
    {
        try
        {
            using var stream = new MemoryStream(trieBytes, writable: false);
            WordTrie trie = WordTrie.Load(stream);
            List<ForgeData> forges = ForgeCatalog.LoadAllFromJson(packTexts);

            // Reject duplicate ids / corrupt checksums before any screen can use them.
            var seen = new HashSet<int>();
            foreach (ForgeData forge in forges)
                if (!seen.Add(forge.ForgeId))
                    throw new InvalidDataException($"duplicate forge id {forge.ForgeId}");

            _parsedTrie = trie;
            _parsedForges = forges;
        }
        catch (Exception ex)
        {
            _parseError = ex.Message;
        }
        finally
        {
            // CallDeferred is the thread-safe way back to the main thread.
            CallDeferred(nameof(PublishData));
        }
    }

    private void PublishData()
    {
        // Always emit exactly once, success or failure: Boot must show an explicit
        // error state rather than hanging on a boot splash forever.
        if (_parseError is null)
        {
            IsReady = true;
            GD.Print($"[Data] ready | dictionary nodes={Dictionary.NodeCount} forges={Forges.Count}");
        }
        EventBus.Instance.EmitSignal(EventBus.SignalName.DataReady);
    }

    private void Fail(string message)
    {
        GD.PushError($"[Data] {message}");
        _parseError = message;
        CallDeferred(nameof(PublishData));
    }

    public ForgeData? ForgeById(int forgeId)
    {
        foreach (ForgeData forge in Forges)
            if (forge.ForgeId == forgeId) return forge;
        return null;
    }
}
