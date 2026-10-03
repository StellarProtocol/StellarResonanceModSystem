using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Finds effect files and their includes the way ReShade does: an include next to the including file first,
/// then each search path in order (a recursive path covers every folder below it). Each search root is walked ONCE
/// into a file-name → full-paths index (case-insensitive, at most <see cref="MaxFilesPerRoot"/> files — past the cap a
/// file is simply not found, so its effect counts as using depth); lookups are dictionary reads until
/// <see cref="ClearCache"/> / <see cref="SetSearchPaths"/>. Rooted includes are refused; results are full paths.</summary>
internal sealed class EffectFileLocator
{
    internal const int MaxFilesPerRoot = 20000;

    private readonly IEffectFileSystem _fs;
    private readonly int _maxFilesPerRoot;
    private readonly List<ReShadeSearchPath> _paths = new();
    private readonly Dictionary<ReShadeSearchPath, Dictionary<string, List<string>>> _index = new();

    internal EffectFileLocator(IEffectFileSystem fs, int maxFilesPerRoot = MaxFilesPerRoot)
    {
        _fs = fs;
        _maxFilesPerRoot = maxFilesPerRoot;
    }

    internal void SetSearchPaths(IEnumerable<string> rawPaths)
    {
        _paths.Clear();
        foreach (var raw in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = ReShadeSearchPath.Parse(raw);
            if (FullPath(path.Root) is { } root) _paths.Add(path with { Root = root });
        }
        ClearCache();
    }

    internal void ClearCache() => _index.Clear();

    internal string? FindEffect(string fileName) => SearchPaths(fileName);

    internal string? ResolveInclude(string includingFile, string name)
    {
        if (Path.IsPathRooted(name)) return null;
        var folder = Path.GetDirectoryName(includingFile);
        if (folder is not null && FullPath(Path.Combine(folder, name)) is { } local && _fs.LastWriteTicks(local) is not null)
            return local;
        return SearchPaths(name);
    }

    private string? SearchPaths(string name)
    {
        var fileName = Path.GetFileName(name);
        if (fileName.Length == 0) return null;
        foreach (var path in _paths)
        {
            if (!IndexOf(path).TryGetValue(fileName, out var candidates)) continue;
            if (Pick(candidates, path.Root, name) is { } found) return found;
        }
        return null;
    }

    // A file directly in the root wins; otherwise the first indexed path ending in the (possibly folder-qualified) name.
    private static string? Pick(List<string> candidates, string root, string name)
    {
        var direct = Unify(Path.Combine(root, name));
        var suffix = "/" + Unify(name).TrimStart('/');
        string? first = null;
        foreach (var candidate in candidates)
        {
            var unified = Unify(candidate);
            if (string.Equals(unified, direct, StringComparison.OrdinalIgnoreCase)) return candidate;
            if (first is null && unified.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) first = candidate;
        }
        return first;
    }

    private Dictionary<string, List<string>> IndexOf(ReShadeSearchPath path)
    {
        if (_index.TryGetValue(path, out var files)) return files;
        files = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in _fs.EnumerateFiles(path.Root, path.Recursive, _maxFilesPerRoot))
        {
            var key = Path.GetFileName(file);
            if (!files.TryGetValue(key, out var list)) files[key] = list = new List<string>(1);
            list.Add(file);
        }
        _index[path] = files;
        return files;
    }

    private static string Unify(string path) => path.Replace('\\', '/');

    private static string? FullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null; // an invalid path is simply not found
        }
    }
}
