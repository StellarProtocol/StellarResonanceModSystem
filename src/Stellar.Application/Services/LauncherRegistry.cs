using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// The plugin-launcher entry list. Feeds both the native rail button and the
/// launcher menu. Plugins (and the framework) call <see cref="Register"/>;
/// the launcher menu reads <see cref="Entries"/>, <see cref="Mode"/>, and the
/// per-entry pin state. Pin/mode are persisted via <see cref="LauncherPrefs"/>.
/// </summary>
internal sealed class LauncherRegistry : ILauncher
{
    private readonly List<LauncherEntry> _entries = new();
    private readonly LauncherPrefs _prefs;
    private int _revision;

    public LauncherRegistry(LauncherPrefs prefs) => _prefs = prefs;

    public IReadOnlyList<LauncherEntry> Entries => _entries;

    /// <summary>
    /// Monotonic counter bumped whenever the derived menu contents change
    /// (entry add/remove or a pin toggle). Lets the launcher window cache its
    /// per-frame projected lists and rebuild only when this differs — avoids
    /// allocating fresh lists/closures on every OnGUI frame the menu is open.
    /// </summary>
    public int Revision => _revision;

    public IDisposable Register(LauncherEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        _entries.Add(entry);
        _revision++;
        return new Registration(this, entry);
    }

    /// <summary>Persisted layout mode (Minimal/Full). Default = Full.</summary>
    public LauncherMode Mode
    {
        get => _prefs.Mode;
        set => _prefs.Mode = value;
    }

    /// <summary>Minimal-mode orientation (false = vertical column, true = horizontal row). Persisted.</summary>
    public bool MinimalHorizontal
    {
        get => _prefs.MinimalHorizontal;
        set => _prefs.MinimalHorizontal = value;
    }

    public bool IsPinned(LauncherEntry entry) => _prefs.IsPinned(entry.Title);

    public void SetPinned(LauncherEntry entry, bool pinned)
    {
        _prefs.SetPinned(entry.Title, pinned);
        _revision++;
    }

    private void Remove(LauncherEntry entry)
    {
        if (_entries.Remove(entry)) _revision++;
    }

    // Removes exactly the registered instance once; idempotent so a double
    // Dispose can't evict a later same-titled entry (List.Remove is by reference
    // equality for the captured instance).
    private sealed class Registration : IDisposable
    {
        private readonly LauncherRegistry _owner;
        private readonly LauncherEntry _entry;
        private bool _disposed;

        public Registration(LauncherRegistry owner, LauncherEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(_entry);
        }
    }
}
