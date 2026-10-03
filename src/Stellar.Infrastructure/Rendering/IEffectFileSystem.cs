using System.Collections.Generic;

namespace Stellar.Infrastructure.Rendering;

/// <summary>The file reads <see cref="EffectDepthIndex"/> needs, so effect lookup and include flattening are testable
/// over an in-memory tree.</summary>
internal interface IEffectFileSystem
{
    /// <summary>Last-write time in ticks, or null when the file does not exist.</summary>
    long? LastWriteTicks(string path);
    /// <summary>The file's text, or null when it cannot be read.</summary>
    string? ReadText(string path);
    /// <summary>Every folder below <paramref name="root"/> (recursive, root itself excluded); empty when missing.</summary>
    IReadOnlyList<string> DirectoriesUnder(string root);
}
