using System;
using System.Collections.Generic;

namespace Stellar.Application.Services;

/// <summary>
/// Pure decision + transform for migrating a launcher pin saved under an entry's OLD displayed title to its
/// CURRENT <see cref="Abstractions.Services.LauncherEntry.Title"/> — the persisted pin identity.
/// </summary>
/// <remarks>
/// Eight released plugins used to pass a TRANSLATED string as <c>LauncherEntry.Title</c> (no
/// <c>TitleProvider</c>); their new builds pass a fixed English <c>Title</c> + a <c>TitleProvider</c> for
/// display. A ja/th/id/fil user who pinned under the old translated title loses the pin once the plugin
/// updates, because <c>LauncherPrefs</c> persists pins keyed by <c>Title</c>. This class computes the pin-set
/// edit needed to carry that pin forward, called once per <c>LauncherRegistry.Register</c> (per entry, not
/// per frame) — see <see cref="LauncherPrefs.MigratePinIfNeeded"/> for the allocation-free gate that only
/// invokes <see cref="Migrate"/> when a stale entry might actually exist.
/// </remarks>
internal static class LauncherPinMigration
{
    /// <summary>
    /// Returns the migrated pin list (same order, the stale <paramref name="displayTitle"/> entry replaced or
    /// dropped), or <c>null</c> when no change is needed — the caller skips persisting on <c>null</c>.
    /// </summary>
    /// <param name="pinned">The current persisted pin set, in its persisted order.</param>
    /// <param name="title">The entry's CURRENT <c>Title</c> — the persistence key going forward.</param>
    /// <param name="displayTitle">The entry's CURRENT displayed title (<c>TitleProvider?.Invoke() ?? Title</c>)
    /// — the title a stale pin might still be saved under, from before the entry had a <c>TitleProvider</c>.</param>
    /// <returns>
    /// <list type="bullet">
    /// <item><c>null</c> — <paramref name="displayTitle"/> equals <paramref name="title"/> (nothing ever
    /// diverged), or <paramref name="pinned"/> doesn't contain <paramref name="displayTitle"/> (no stale
    /// pin to carry forward). Both are the steady-state case once every affected user has migrated once.</item>
    /// <item>a list with <paramref name="displayTitle"/> replaced in place by <paramref name="title"/> — the
    /// ordinary migration, when <paramref name="title"/> was not already separately pinned.</item>
    /// <item>a list with <paramref name="displayTitle"/> dropped and <paramref name="title"/> left at its own
    /// existing position — when BOTH are already present (e.g. the user pinned again under the new title
    /// before this migration ran), so the result never has a duplicate.</item>
    /// </list>
    /// </returns>
    public static IReadOnlyList<string>? Migrate(IReadOnlyList<string> pinned, string title, string displayTitle)
    {
        if (string.Equals(displayTitle, title, StringComparison.Ordinal)) return null;
        if (!Contains(pinned, displayTitle)) return null;

        var titleAlreadyPinned = Contains(pinned, title);
        var result = new List<string>(pinned.Count - (titleAlreadyPinned ? 1 : 0));
        foreach (var p in pinned)
        {
            if (string.Equals(p, displayTitle, StringComparison.Ordinal))
            {
                if (!titleAlreadyPinned) result.Add(title);   // migrate in place
                // else: drop — title already holds its own position elsewhere in the list
            }
            else
            {
                result.Add(p);
            }
        }
        return result;
    }

    private static bool Contains(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return true;
        return false;
    }
}
