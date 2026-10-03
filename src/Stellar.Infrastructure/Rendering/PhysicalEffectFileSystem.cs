using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Infrastructure.Rendering;

/// <summary><see cref="IEffectFileSystem"/> over the real disk. Every failure reads as "missing", and a file larger
/// than <see cref="MaxFileBytes"/> as unreadable (so it counts as using depth).</summary>
internal sealed class PhysicalEffectFileSystem : IEffectFileSystem
{
    internal const int MaxFileBytes = 4 * 1024 * 1024;

    public long? LastWriteTicks(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : null;
        }
        catch (Exception)
        {
            return null; // unreadable reads as missing, which callers treat as "uses depth"
        }
    }

    public string? ReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null; // unreadable source → callers assume depth (D8-safe)
        }
    }

    public IReadOnlyList<string> EnumerateFiles(string root, bool recursive, int limit)
    {
        var files = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return files;
            var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                if (files.Count >= limit) break;
                files.Add(file);
            }
        }
        catch (Exception)
        {
            // a folder that vanished mid-walk: keep what was listed
        }
        return files;
    }
}
