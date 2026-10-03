using System;
using System.Collections.Generic;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Per effect file name (as ReShade reports it), whether its source — includes flattened — reads the depth buffer.
/// D8 safety: an effect that cannot be found, read or fully flattened counts as using depth, so a shaped photo
/// switches it off rather than drawing it with a wrong depth buffer.
/// <para>Results are cached. <see cref="MarkStale"/> (called after ReShade reloads) makes each entry re-check the
/// last-write time of every file it read and re-scan only when one changed; <see cref="SetSearchPaths"/> forgets
/// everything. Main thread only.</para>
/// </summary>
internal sealed class EffectDepthIndex
{
    private sealed class Entry
    {
        internal bool UsesDepth;
        internal bool Stale;
        internal List<(string Path, long Ticks)>? Files; // null = the effect was not found or not readable
    }

    private readonly IEffectFileSystem _fs;
    private readonly EffectFileLocator _locator;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Ticks, string Text)> _texts = new(StringComparer.OrdinalIgnoreCase);

    internal EffectDepthIndex(IEffectFileSystem fs)
    {
        _fs = fs;
        _locator = new EffectFileLocator(fs);
    }

    internal void SetSearchPaths(IReadOnlyList<string> rawPaths)
    {
        _locator.SetSearchPaths(rawPaths);
        _entries.Clear();
        _texts.Clear();
    }

    internal void MarkStale()
    {
        _locator.ClearCache();
        foreach (var entry in _entries.Values)
            entry.Stale = true;
    }

    /// <summary>The last result for <paramref name="effectFile"/> (possibly stale), or null when never resolved.</summary>
    internal bool? Known(string effectFile) => _entries.TryGetValue(effectFile, out var e) ? e.UsesDepth : null;

    internal bool NeedsWork(string effectFile) => !_entries.TryGetValue(effectFile, out var e) || e.Stale;

    internal bool Resolve(string effectFile)
    {
        if (_entries.TryGetValue(effectFile, out var entry) && entry.Stale && Unchanged(entry.Files))
        {
            entry.Stale = false;
            return entry.UsesDepth;
        }
        entry = Scan(effectFile);
        _entries[effectFile] = entry;
        return entry.UsesDepth;
    }

    private bool Unchanged(List<(string Path, long Ticks)>? files)
    {
        if (files is null) return false;
        foreach (var (path, ticks) in files)
        {
            if (_fs.LastWriteTicks(path) != ticks) return false;
        }
        return true;
    }

    // Every candidate file (an ambiguous name in a recursive root) is scanned and the results ORed, and every
    // candidate of an ambiguous include is inlined — the answer never depends on the folder walk's order (D8).
    private Entry Scan(string effectFile)
    {
        var paths = _locator.FindEffectCandidates(effectFile);
        if (paths.Count == 0) return new Entry { UsesDepth = true };
        var files = new List<(string Path, long Ticks)>();
        var usesDepth = false;
        foreach (var path in paths)
        {
            var source = EffectIncludeFlattener.Flatten(path, p => ReadTracked(p, files), _locator.ResolveIncludeCandidates);
            if (source is null) return new Entry { UsesDepth = true };
            usesDepth |= EffectDepthScanner.UsesDepth(source);
        }
        return new Entry { UsesDepth = usesDepth, Files = files };
    }

    private string? ReadTracked(string path, List<(string Path, long Ticks)> files)
    {
        if (_fs.LastWriteTicks(path) is not long ticks) return null;
        if (!_texts.TryGetValue(path, out var cached) || cached.Ticks != ticks)
        {
            var text = _fs.ReadText(path);
            if (text is null) return null;
            cached = (ticks, text);
            _texts[path] = cached;
        }
        files.Add((path, ticks));
        return cached.Text;
    }
}
