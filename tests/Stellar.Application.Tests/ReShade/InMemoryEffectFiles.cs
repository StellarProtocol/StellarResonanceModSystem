using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Infrastructure.Rendering;

namespace Stellar.Application.Tests.ReShade;

/// <summary>In-memory <see cref="IEffectFileSystem"/> over '/'-separated absolute paths, counting reads.</summary>
internal sealed class InMemoryEffectFiles : IEffectFileSystem
{
    private readonly Dictionary<string, (string Text, long Ticks)> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unreadable = new(StringComparer.Ordinal);

    public Dictionary<string, int> Reads { get; } = new(StringComparer.Ordinal);
    public int DirectoryScans { get; private set; }

    public void Add(string path, string text, long ticks = 1) => _files[path] = (text, ticks);
    public void Touch(string path, string text) => _files[path] = (text, _files[path].Ticks + 1);
    public void MarkUnreadable(string path) => _unreadable.Add(path);
    public int ReadsOf(string path) => Reads.TryGetValue(path, out var n) ? n : 0;

    public long? LastWriteTicks(string path) => _files.TryGetValue(path, out var f) ? f.Ticks : null;

    public string? ReadText(string path)
    {
        Reads[path] = ReadsOf(path) + 1;
        if (_unreadable.Contains(path)) return null;
        return _files.TryGetValue(path, out var f) ? f.Text : null;
    }

    public IReadOnlyList<string> DirectoriesUnder(string root)
    {
        DirectoryScans++;
        var prefix = root.TrimEnd('/') + "/";
        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in _files.Keys)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length >= prefix.Length; dir = Path.GetDirectoryName(dir))
                dirs.Add(dir);
        }
        return new List<string>(dirs);
    }
}
