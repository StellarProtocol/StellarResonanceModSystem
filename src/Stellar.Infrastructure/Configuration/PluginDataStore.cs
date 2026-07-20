using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// File-backed <see cref="IPluginDataStore"/> rooted at
/// <c>&lt;baseDir&gt;/&lt;pluginGuid&gt;.data/</c>. Pure BCL IO; never throws.
/// </summary>
internal sealed class PluginDataStore : IPluginDataStore
{
    private readonly string _dataDir;
    private readonly IPluginLog _log;

    public PluginDataStore(string baseDirPath, string pluginGuid, IPluginLog log)
    {
        _dataDir = Path.GetFullPath(Path.Combine(baseDirPath, pluginGuid + ".data"));
        _log = log;
    }

    public void Write(string name, byte[] data)
    {
        if (!TryResolve(_dataDir, name, out var full)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            var tmp = full + ".tmp";
            File.WriteAllBytes(tmp, data);
            // net6.0+ 3-arg overload replaces atomically; no Delete-then-Move gap where `full` is absent.
            File.Move(tmp, full, overwrite: true);
        }
        catch (Exception ex) { _log.Warning($"[Stellar][PluginData] write {name} failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    public byte[]? Read(string name)
    {
        if (!TryResolve(_dataDir, name, out var full)) return null;
        try { return File.Exists(full) ? File.ReadAllBytes(full) : null; }
        catch (Exception ex) { _log.Warning($"[Stellar][PluginData] read {name} failed: {ex.GetType().Name}: {ex.Message}"); return null; }
    }

    public void Delete(string name)
    {
        if (!TryResolve(_dataDir, name, out var full)) return;
        try { if (File.Exists(full)) File.Delete(full); }
        catch (Exception ex) { _log.Warning($"[Stellar][PluginData] delete {name} failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    public IReadOnlyList<string> List(string? prefix = null)
    {
        try
        {
            if (!Directory.Exists(_dataDir)) return Array.Empty<string>();
            var results = new List<string>();
            foreach (var path in Directory.EnumerateFiles(_dataDir, "*", SearchOption.AllDirectories))
            {
                if (path.EndsWith(".tmp", StringComparison.Ordinal)) continue;
                var rel = path.Substring(_dataDir.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                              .Replace(Path.DirectorySeparatorChar, '/');
                if (prefix is null || rel.StartsWith(prefix, StringComparison.Ordinal)) results.Add(rel);
            }
            return results;
        }
        catch (Exception ex) { _log.Warning($"[Stellar][PluginData] list failed: {ex.GetType().Name}: {ex.Message}"); return Array.Empty<string>(); }
    }

    /// <summary>Resolves a relative name to a full path under <paramref name="dataDir"/>. Rejects empty,
    /// rooted, backslash-containing, or <c>..</c>-containing names, and names with more than one
    /// <c>/</c> separator. Pure — unit-tested directly.</summary>
    internal static bool TryResolve(string dataDir, string name, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrEmpty(name)) return false;
        if (name.IndexOf('\\') >= 0) return false;
        try
        {
            if (Path.IsPathRooted(name)) return false;
            var segments = name.Split('/');
            if (segments.Length > 2) return false;              // at most one subdir
            foreach (var seg in segments)
                if (seg.Length == 0 || seg == "." || seg == "..") return false;
            var candidate = Path.GetFullPath(Path.Combine(dataDir, name));
            var root = dataDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ? dataDir : dataDir + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.Ordinal)) return false;   // defense in depth
            fullPath = candidate;
            return true;
        }
        catch (Exception)
        {
            // Embedded NUL (or other platform-specific invalid path chars, e.g. ':' under Wine/Windows)
            // makes Path.IsPathRooted/GetFullPath throw. TryResolve must be total — never throw.
            return false;
        }
    }
}
