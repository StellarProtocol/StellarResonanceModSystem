using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Finds effect files and their includes the way ReShade does: an include next to the including file
/// first, then each search path in order (a recursive path searches every folder below it). Folder listings are
/// cached until <see cref="ClearCache"/> / <see cref="SetSearchPaths"/>.</summary>
internal sealed class EffectFileLocator
{
    private readonly IEffectFileSystem _fs;
    private readonly List<ReShadeSearchPath> _paths = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _dirs = new(StringComparer.Ordinal);

    internal EffectFileLocator(IEffectFileSystem fs) => _fs = fs;

    internal void SetSearchPaths(IEnumerable<string> rawPaths)
    {
        _paths.Clear();
        foreach (var raw in rawPaths)
        {
            if (!string.IsNullOrWhiteSpace(raw)) _paths.Add(ReShadeSearchPath.Parse(raw));
        }
        ClearCache();
    }

    internal void ClearCache() => _dirs.Clear();

    internal string? FindEffect(string fileName) => SearchPaths(fileName);

    internal string? ResolveInclude(string includingFile, string name)
    {
        var folder = Path.GetDirectoryName(includingFile);
        if (folder is not null)
        {
            var local = Path.Combine(folder, name);
            if (_fs.LastWriteTicks(local) is not null) return local;
        }
        return SearchPaths(name);
    }

    private string? SearchPaths(string name)
    {
        foreach (var path in _paths)
        {
            var direct = Path.Combine(path.Root, name);
            if (_fs.LastWriteTicks(direct) is not null) return direct;
            if (!path.Recursive) continue;
            foreach (var dir in DirectoriesUnder(path.Root))
            {
                var candidate = Path.Combine(dir, name);
                if (_fs.LastWriteTicks(candidate) is not null) return candidate;
            }
        }
        return null;
    }

    private IReadOnlyList<string> DirectoriesUnder(string root)
    {
        if (!_dirs.TryGetValue(root, out var dirs))
        {
            dirs = _fs.DirectoriesUnder(root);
            _dirs[root] = dirs;
        }
        return dirs;
    }
}
