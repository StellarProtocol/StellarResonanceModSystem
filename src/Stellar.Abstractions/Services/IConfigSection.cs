// src/Stellar.Abstractions/Services/IConfigSection.cs
namespace Stellar.Abstractions.Services;

/// <summary>
/// A named subset of a plugin's configuration. Read access via
/// <see cref="Get{T}"/> is lock-free against the in-memory cache; writes via
/// <see cref="Set{T}"/> are cached and persisted to disk on <see cref="Save"/>.
///
/// Supported <c>T</c>: primitives (<see cref="int"/>, <see cref="long"/>,
/// <see cref="bool"/>, <see cref="string"/>, <see cref="float"/>,
/// <see cref="double"/>), arrays of primitives, and string-keyed dictionaries
/// of primitives. Records and complex objects are out of scope for v1 — use
/// multiple keys instead.
/// </summary>
public interface IConfigSection
{
    /// <summary>
    /// Returns the value stored at <paramref name="key"/>, or
    /// <paramref name="defaultValue"/> if the key is absent or the stored
    /// value can't be coerced to <typeparamref name="T"/>. Never throws.
    /// </summary>
    T? Get<T>(string key, T? defaultValue);

    /// <summary>
    /// Stores <paramref name="value"/> at <paramref name="key"/> in the
    /// in-memory cache. Does NOT write to disk — caller must invoke
    /// <see cref="Save"/> when batch of edits is complete.
    /// </summary>
    void Set<T>(string key, T value);

    /// <summary>
    /// Flushes the in-memory cache for this section to disk, then fires
    /// <see cref="IPluginConfig.SectionChanged"/>. Cross-process /
    /// external-edit detection is handled by the framework — plugins don't
    /// poll the file.
    /// </summary>
    void Save();

    /// <summary>Persists the section without raising <see cref="IPluginConfig.SectionChanged"/> — for echo-suppression
    /// when the writer is reacting to its own change.</summary>
    void SaveQuiet();
}
