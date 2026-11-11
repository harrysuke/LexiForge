using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LexiForge.Core;

/// <summary>
/// Pure C# trie for the ~60k-word offline dictionary and the level generator's
/// prefix pruning. O(len) lookup, zero allocations on the hot path.
///
/// Runtime loads a compact binary image built by tools/LevelGen; the generator
/// uses Add()/EnumerateFromLetters(). Never allocate in a drag-input callback.
/// </summary>
public sealed class WordTrie
{
    private readonly List<Dictionary<char, int>> _edges = new();
    private readonly List<bool> _terminal = new();

    public int NodeCount => _edges.Count;

    public WordTrie()
    {
        _edges.Add(new Dictionary<char, int>());
        _terminal.Add(false);
    }

    public void Add(ReadOnlySpan<char> word)
    {
        // Validate first: rejecting a word half-way through used to leave orphan
        // partial nodes in the trie, which later produced phantom prefixes.
        foreach (char raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (c is < 'a' or > 'z') return;
        }

        int node = 0;
        foreach (char raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (!_edges[node].TryGetValue(c, out int next))
            {
                next = _edges.Count;
                _edges[node][c] = next;
                _edges.Add(new Dictionary<char, int>());
                _terminal.Add(false);
            }
            node = next;
        }
        _terminal[node] = true;
    }

    public bool Contains(ReadOnlySpan<char> word)
    {
        int node = 0;
        foreach (char raw in word)
        {
            char c = char.ToLowerInvariant(raw);
            if (!_edges[node].TryGetValue(c, out int next)) return false;
            node = next;
        }
        return _terminal[node];
    }

    public bool HasPrefix(ReadOnlySpan<char> prefix)
    {
        int node = 0;
        foreach (char raw in prefix)
        {
            char c = char.ToLowerInvariant(raw);
            if (!_edges[node].TryGetValue(c, out int next)) return false;
            node = next;
        }
        return true;
    }

    /// <summary>
    /// All dictionary words constructible from the given letter multiset.
    /// Used offline by the forge generator; not called at runtime.
    /// </summary>
    public List<string> EnumerateFromLetters(ReadOnlySpan<char> bank, int maxResults = 4096)
    {
        var counts = new int[26];
        int wilds = 0;
        foreach (char raw in bank)
        {
            char c = char.ToLowerInvariant(raw);
            if (c == '*') wilds++;
            else if (c is >= 'a' and <= 'z') counts[c - 'a']++;
        }

        var results = new List<string>();
        var buffer = new StringBuilder(12);
        Walk(0, counts, wilds, buffer, results, maxResults);
        return results;
    }

    /// <summary>
    /// A '*' tile (Gale rune) substitutes for exactly ONE missing letter. The old
    /// implementation added +1 to every letter count, which incorrectly allowed a
    /// word to consume several wildcards at once.
    /// </summary>
    private void Walk(int node, int[] counts, int wilds, StringBuilder buffer,
                      List<string> results, int maxResults)
    {
        if (results.Count >= maxResults) return;
        if (_terminal[node] && buffer.Length >= 2) results.Add(buffer.ToString());

        foreach (var (c, next) in _edges[node])
        {
            int idx = c - 'a';
            bool usedWild = false;
            if (counts[idx] > 0) counts[idx]--;
            else if (wilds > 0) { wilds--; usedWild = true; }
            else continue;

            buffer.Append(c);
            Walk(next, counts, wilds, buffer, results, maxResults);
            buffer.Length--;

            if (usedWild) wilds++;
            else counts[idx]++;
        }
    }

    // ---- Compact binary image: [int nodeCount][per node: int edgeCount, (byte char,int target)*][byte flags] ----
    // edgeCount is a full Int32: writing it as a byte while still emitting every edge
    // silently corrupted any node with more than 255 children.
    public void Save(string path)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(_edges.Count);
        foreach (var edges in _edges)
        {
            w.Write(edges.Count);
            foreach (var (c, target) in edges) { w.Write((byte)c); w.Write(target); }
        }
        foreach (bool terminal in _terminal) w.Write((byte)(terminal ? 1 : 0));
    }

    public static WordTrie Load(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    /// <summary>
    /// Loads a trie image from any seekable stream. On Android, res:// files live
    /// inside the APK/PCK and have no usable OS path, so the runtime reads the
    /// bytes with Godot's FileAccess and hands a MemoryStream to this overload.
    /// Keeping the parser here (not in Godot-facing code) means the exact same
    /// binary format is validated by CoreTests and consumed by the game.
    /// </summary>
    public static WordTrie Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        int count = r.ReadInt32();
        if (count <= 0 || count > 10_000_000) throw new InvalidDataException("Corrupt words_en.dat: bad node count.");
        var trie = new WordTrie();
        trie._edges.Clear();
        trie._terminal.Clear();
        for (int i = 0; i < count; i++)
        {
            var edges = new Dictionary<char, int>();
            int edgeCount = r.ReadInt32();
            for (int e = 0; e < edgeCount; e++) edges.Add((char)r.ReadByte(), r.ReadInt32());
            trie._edges.Add(edges);
        }
        for (int i = 0; i < count; i++) trie._terminal.Add(r.ReadByte() != 0);
        return trie;
    }
}
