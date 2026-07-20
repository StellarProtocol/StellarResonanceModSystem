// src/Stellar.Abstractions/Services/IPluginDataStore.cs
using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-plugin binary file storage for data too large or too opaque for
/// <see cref="IConfigSection"/> (which holds only primitives, arrays, and
/// dictionaries). Each plugin gets its own directory
/// (<c>&lt;plugins-dir&gt;/&lt;pluginGuid&gt;.data/</c>); names are relative to it.
/// A <c>name</c> may contain at most one <c>/</c> separating a single
/// subdirectory from the file (e.g. <c>replay/123-456.gz</c>); <c>..</c>, rooted
/// paths, and backslashes are rejected. Every method is best-effort and NEVER
/// throws — IO faults are logged and swallowed.
/// </summary>
public interface IPluginDataStore
{
    /// <summary>Write <paramref name="data"/> to the named file, overwriting any existing content. No-op on IO failure.</summary>
    /// <param name="name">Relative file name (see type remarks for the allowed shape).</param>
    /// <param name="data">The bytes to store.</param>
    void Write(string name, byte[] data);

    /// <summary>Read the named file, or <c>null</c> if it is absent, the name is invalid, or the read fails.</summary>
    /// <param name="name">Relative file name (see type remarks for the allowed shape).</param>
    /// <returns>The stored bytes, or <c>null</c>.</returns>
    byte[]? Read(string name);

    /// <summary>Delete the named file. No-op when the file is absent, the name is invalid, or the delete fails.</summary>
    /// <param name="name">Relative file name (see type remarks for the allowed shape).</param>
    void Delete(string name);

    /// <summary>Names of existing files in this plugin's data directory, optionally filtered to those starting with <paramref name="prefix"/>. Returns an empty list on any failure. Names use <c>/</c> as the separator.</summary>
    /// <param name="prefix">Optional name prefix filter (e.g. <c>"replay/"</c>); <c>null</c> returns all.</param>
    /// <returns>Matching file names, relative to the data directory.</returns>
    IReadOnlyList<string> List(string? prefix = null);
}
