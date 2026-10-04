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

    /// <summary>Every file the effect name could be. Usually one; several when a recursive root holds the name in more
    /// than one subfolder and none sits directly in the root — which one ReShade loads then depends on its own walk
    /// order, so a D8 caller must treat them all as possible (OR their depth use).</summary>
    internal IReadOnlyList<string> FindEffectCandidates(string fileName) => SearchPaths(fileName);

    /// <summary>Every file an include could resolve to (see <see cref="FindEffectCandidates"/>); empty when none.</summary>
    internal IReadOnlyList<string> ResolveIncludeCandidates(string includingFile, string name)
    {
        if (Path.IsPathRooted(name)) return Array.Empty<string>();
        var folder = Path.GetDirectoryName(includingFile);
        if (folder is not null && FullPath(Path.Combine(folder, name)) is { } local && _fs.LastWriteTicks(local) is not null)
            return new[] { local };
        return SearchPaths(name);
    }

    private IReadOnlyList<string> SearchPaths(string name)
    {
        var fileName = Path.GetFileName(name);
        if (fileName.Length == 0) return Array.Empty<string>();
        foreach (var path in _paths)
        {
            if (!IndexOf(path).TryGetValue(fileName, out var candidates)) continue;
            var matched = Matches(candidates, path.Root, name);
            if (matched.Count > 0) return matched;
        }
        return Array.Empty<string>();
    }

    // A file directly in the root is the only answer; otherwise EVERY indexed path ending in the (possibly
    // folder-qualified) name — never just the first, whose identity depends on the folder walk's order.
    private static IReadOnlyList<string> Matches(List<string> candidates, string root, string name)
    {
        var direct = Unify(Path.Combine(root, name));
        var suffix = "/" + Unify(name).TrimStart('/');
        List<string>? found = null;
        foreach (var candidate in candidates)
        {
            var unified = Unify(candidate);
            if (string.Equals(unified, direct, StringComparison.OrdinalIgnoreCase)) return new[] { candidate };
            if (unified.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) (found ??= new List<string>(1)).Add(candidate);
        }
        return (IReadOnlyList<string>?)found ?? Array.Empty<string>();
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
