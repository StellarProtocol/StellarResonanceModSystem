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

    /// <summary>A custom theme was renamed: moves every override of <paramref name="oldName"/> to
    /// <paramref name="newName"/>. Stale entries already under <paramref name="newName"/> (orphans of an earlier
    /// theme with that name) are DROPPED first, so the renamed theme carries exactly its own colours. STAGES the
    /// result into config without saving — the caller's Save writes it (see <see cref="CustomThemeStore.Rename"/>).
    /// Returns whether anything changed.</summary>
    bool RenameTheme(string oldName, string newName);

    /// <summary>Drops every override stored under <paramref name="themeName"/>; staged like <see cref="RenameTheme"/>.
    /// Returns whether anything was removed.</summary>
    bool RemoveTheme(string themeName);

    /// <summary>A custom theme was duplicated: copies every override of <paramref name="fromName"/> to
    /// <paramref name="toName"/>, leaving the source as it is. Stale entries already under <paramref name="toName"/> are
    /// DROPPED first (like <see cref="RenameTheme"/>), so the copy carries exactly the source's colours. Staged, not
    /// saved — the caller's Save writes it (see <see cref="CustomThemeStore.Duplicate"/>). Returns whether anything
    /// changed.</summary>
    bool CopyTheme(string fromName, string toName);
}
