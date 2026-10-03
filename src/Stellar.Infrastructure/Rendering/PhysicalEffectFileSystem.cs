using System;
using System.Collections.Generic;
using System.IO;

namespace Stellar.Infrastructure.Rendering;

/// <summary><see cref="IEffectFileSystem"/> over the real disk. Every failure reads as "missing".</summary>
internal sealed class PhysicalEffectFileSystem : IEffectFileSystem
{
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
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return null; // unreadable source → callers assume depth (D8-safe)
        }
    }

    public IReadOnlyList<string> DirectoriesUnder(string root)
    {
        var dirs = new List<string>();
        try
        {
            if (!Directory.Exists(root)) return dirs;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            dirs.AddRange(Directory.EnumerateDirectories(root, "*", options));
        }
        catch (Exception)
        {
            // a folder that vanished mid-walk: keep what was listed
        }
        return dirs;
    }
}
