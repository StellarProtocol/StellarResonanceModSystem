using System.Collections.Generic;

namespace Stellar.Infrastructure.Rendering;

/// <summary>The file reads <see cref="EffectScanIndex"/> needs, so effect lookup and include flattening are testable
/// over an in-memory tree.</summary>
internal interface IEffectFileSystem
{
    /// <summary>Last-write time in ticks, or null when the file does not exist.</summary>
    long? LastWriteTicks(string path);
    /// <summary>The file's text, or null when it cannot be read (or is too large to be an effect source).</summary>
    string? ReadText(string path);
    /// <summary>Full paths of the files in <paramref name="root"/> (and below it when <paramref name="recursive"/>),
    /// at most <paramref name="limit"/> of them; empty when the folder is missing.</summary>
    IReadOnlyList<string> EnumerateFiles(string root, bool recursive, int limit);
}
