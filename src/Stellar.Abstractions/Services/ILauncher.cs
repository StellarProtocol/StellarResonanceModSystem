using System;
using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>How the launcher menu lays itself out.</summary>
public enum LauncherMode
{
    /// <summary>Compact vertical column of pinned entries (+ Settings).</summary>
    Minimal,

    /// <summary>Grouped tile grid showing every entry, with pin toggles.</summary>
    Full,
}

/// <summary>Which Full-mode section an entry sits in.</summary>
public enum LauncherGroup
{
    /// <summary>A tool plugin — large tile, pinnable (shows in Minimal when pinned).</summary>
    Plugin,

    /// <summary>A framework Settings panel — smaller tile, opens its panel; not pinned.</summary>
    Settings,
}

/// <summary>
/// A plugin (or the framework) entry shown in the Stellar launcher menu.
/// <paramref name="Title"/> doubles as the stable identity used to persist the
/// entry's pinned state, so keep it unique and stable across sessions.
/// <paramref name="IconPng"/> (raw PNG bytes) is the preferred icon — it
/// rasterises to a native-looking sprite; <paramref name="IconKey"/> is the
/// font-glyph fallback. <paramref name="OnOpen"/> runs when the user clicks the tile.
/// </summary>
public sealed record LauncherEntry(string Title, byte[]? IconPng, string? IconKey, Action OnOpen)
{
    /// <summary>Full-mode section. Plugins default to <see cref="LauncherGroup.Plugin"/>.</summary>
    public LauncherGroup Group { get; init; } = LauncherGroup.Plugin;
}

/// <summary>
/// Registry of launcher entries. Plugins call <see cref="Register"/> to add a
/// tile to the Stellar launcher menu and hold the returned <see cref="IDisposable"/>
/// to remove it on unload. <see cref="Entries"/> preserves registration order.
/// </summary>
public interface ILauncher
{
    /// <summary>Add <paramref name="entry"/>; dispose the result to remove it.</summary>
    IDisposable Register(LauncherEntry entry);

    /// <summary>Registered entries, in registration order.</summary>
    IReadOnlyList<LauncherEntry> Entries { get; }
}
