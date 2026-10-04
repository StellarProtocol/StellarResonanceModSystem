using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Per effect file name (as ReShade reports it), one yes/no fact about its source — includes flattened — decided by a
/// pure scan (<see cref="EffectDepthIndex"/>: reads the depth buffer; <see cref="EffectSizeLockIndex"/>: declares a
/// screen-sized texture). Fail-safe: an effect that cannot be found, read or fully flattened reads as TRUE, so a photo
/// leaves it out (or stays at screen size) rather than drawing it wrong.
/// <para>Results are cached. <see cref="MarkStale"/> (called after ReShade reloads) makes each entry re-check the
/// last-write time of every file it read and re-scan only when one changed; <see cref="SetSearchPaths"/> forgets
/// everything. Main thread only.</para>
/// </summary>
internal abstract class EffectScanIndex
{
    private sealed class Entry
    {
        internal bool Flag;
        internal bool Stale;
        internal List<(string Path, long Ticks)>? Files; // null = the effect was not found or not readable
    }

    private readonly IEffectFileSystem _fs;
    private readonly EffectFileLocator _locator;
    private readonly Func<string?, bool> _scan;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Ticks, string Text)> _texts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="scan">The pure scan over a flattened source (true = flagged).</param>
    protected EffectScanIndex(IEffectFileSystem fs, Func<string?, bool> scan)
    {
        _fs = fs;
        _locator = new EffectFileLocator(fs);
        _scan = scan;
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
    internal bool? Known(string effectFile) => _entries.TryGetValue(effectFile, out var e) ? e.Flag : null;

    internal bool NeedsWork(string effectFile) => !_entries.TryGetValue(effectFile, out var e) || e.Stale;

    internal bool Resolve(string effectFile)
    {
        if (_entries.TryGetValue(effectFile, out var entry) && entry.Stale && Unchanged(entry.Files))
        {
            entry.Stale = false;
            return entry.Flag;
        }
        entry = Scan(effectFile);
        _entries[effectFile] = entry;
        return entry.Flag;
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
        if (paths.Count == 0) return new Entry { Flag = true };
        var files = new List<(string Path, long Ticks)>();
        var flag = false;
        foreach (var path in paths)
        {
            var source = EffectIncludeFlattener.Flatten(path, p => ReadTracked(p, files), _locator.ResolveIncludeCandidates);
            if (source is null) return new Entry { Flag = true };
            flag |= _scan(source);
        }
        return new Entry { Flag = flag, Files = files };
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
