// src/Stellar.Application/Services/IColorOverrideStore.cs
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Application-internal sparse override store: only keys the user
/// changed, keyed by (custom-theme name, slot key). Implemented by
/// ColorOverrideStore (Task 5); stubbed in tests.</summary>
public interface IColorOverrideStore
{
    bool TryGet(string themeName, string slotKey, out ColorRgba value);

    /// <summary>Update the override in memory. Does NOT persist — call
    /// <see cref="Flush"/> to write to disk (the editor flushes on mouse-release
    /// so a slider drag yields one write, not one per 0–255 step).</summary>
    void Set(string themeName, string slotKey, ColorRgba value);
    void Clear(string themeName, string slotKey);
    bool Has(string themeName, string slotKey);

    /// <summary>Persist pending in-memory changes to config if any are dirty.</summary>
    void Flush();
}
