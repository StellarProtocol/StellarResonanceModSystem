using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// The plugin-launcher entry list. Feeds both the native rail button and the
/// launcher menu. Plugins (and the framework) call <see cref="Register"/>;
/// the launcher menu reads <see cref="Entries"/>, <see cref="Mode"/>, and the
/// per-entry pin state. Pin/mode are persisted via <see cref="LauncherPrefs"/>.
/// </summary>
internal sealed class LauncherRegistry : ILauncher, ILauncherOwnedRegistrations
{
    private readonly List<LauncherEntry> _entries = new();
    // Owning plugin guid per registered INSTANCE (reference keys: LauncherEntry is a record, so two plugins'
    // value-equal entries must not share an owner). Absent = registered untagged (the framework's own tiles,
    // or a plugin host without PerPluginLauncher).
    private readonly Dictionary<LauncherEntry, string> _owners = new(ByReference.Instance);
    private readonly LauncherPrefs _prefs;
    private readonly IPluginLog _log;
    private int _revision;

    public LauncherRegistry(LauncherPrefs prefs, IPluginLog log)
    {
        _prefs = prefs;
        _log = log;
    }

    public IReadOnlyList<LauncherEntry> Entries => _entries;

    /// <summary>
    /// Monotonic counter bumped whenever the derived menu contents change
    /// (entry add/remove or a pin toggle). Lets the launcher window cache its
    /// per-frame projected lists and rebuild only when this differs — avoids
    /// allocating fresh lists/closures on every OnGUI frame the menu is open.
    /// </summary>
    public int Revision => _revision;

    public IDisposable Register(LauncherEntry entry) => RegisterCore(entry, ownerId: null);

    /// <summary>Owner-tagged register, used by <c>PerPluginLauncher</c>.</summary>
    IDisposable ILauncherOwnedRegistrations.Register(LauncherEntry entry, string ownerId) => RegisterCore(entry, ownerId);

    private IDisposable RegisterCore(LauncherEntry entry, string? ownerId)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        TryMigratePin(entry);
        _entries.Add(entry);
        if (ownerId is not null) _owners[entry] = ownerId;
        _revision++;
        return new Registration(this, entry);
    }

    /// <inheritdoc />
    public LauncherEntry? FirstEntryOwnedBy(string ownerId)
    {
        foreach (var e in _entries)
            if (_owners.TryGetValue(e, out var owner) && string.Equals(owner, ownerId, StringComparison.Ordinal))
                return e;
        return null;
    }

    // Carries forward a pin saved under this entry's OLD displayed title (before it had a TitleProvider) to
    // its CURRENT Title — see LauncherPrefs.MigratePinIfNeeded. Reading DisplayTitle HERE, at Register, is
    // deliberate, not an incidental early read: it is the SAME moment in plugin load the entry's OLD build
    // read its (translated) Title at, so reproducing that read here reproduces the state the stale pin was
    // saved under — which is what makes the saved key match. It adds no NEW early read either: every
    // TitleProvider in the current catalog is itself just `_loc.T(key)`, already resolved no later than this
    // point by the plugin's own Register call (owner review round 4, check 1).
    private void TryMigratePin(LauncherEntry entry)
    {
        // DisplayTitle runs plugin code (TitleProvider) — fail-safe, like WindowService.SafeApply's
        // ShouldRender guard: a throwing provider must never abort the plugin's Register call, only skip
        // THIS entry's migration.
        string displayTitle;
        try { displayTitle = entry.DisplayTitle; }
        catch (Exception ex)
        {
            _log.Warning($"[Launcher] '{entry.Title}' TitleProvider threw while checking for a pin to migrate; skipped: {ex.Message}");
            return;
        }
        // Collision guard: never migrate into or out of a title another already-registered entry owns (no
        // entry in today's catalog collides — see the review's check 3 — but a future name clash must not
        // silently steal or drop a DIFFERENT plugin's pin). _entries never yet contains THIS entry here —
        // Register adds it only after TryMigratePin returns — so every match found is necessarily another one.
        foreach (var other in _entries)
            if (string.Equals(other.Title, displayTitle, StringComparison.Ordinal)) return;
        _prefs.MigratePinIfNeeded(entry.Title, displayTitle);
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
        // By REFERENCE: LauncherEntry is a record, so List.Remove would match a value-equal entry another
        // registration owns.
        var i = _entries.FindIndex(e => ReferenceEquals(e, entry));
        if (i < 0) return;
        _entries.RemoveAt(i);
        _revision++;
        // Drop the owner only once no copy of this instance remains (the same instance registered twice keeps it).
        if (_entries.FindIndex(e => ReferenceEquals(e, entry)) < 0) _owners.Remove(entry);
    }

    // Reference-identity comparer (BCL ReferenceEqualityComparer is .NET 5+; this file also compiles into the
    // netstandard2.1 UI sandbox).
    private sealed class ByReference : IEqualityComparer<LauncherEntry>
    {
        public static readonly ByReference Instance = new();
        public bool Equals(LauncherEntry? x, LauncherEntry? y) => ReferenceEquals(x, y);
        public int GetHashCode(LauncherEntry obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    // Removes exactly the registered instance once; idempotent so a double Dispose can't evict a later
    // same-titled entry. Removal is by reference (Remove → FindIndex/ReferenceEquals), not record value equality.
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
