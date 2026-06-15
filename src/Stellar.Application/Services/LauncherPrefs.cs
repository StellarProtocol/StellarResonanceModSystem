using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Persists the launcher menu's user preferences — the chosen
/// <see cref="LauncherMode"/> and the set of pinned entry ids — through the
/// framework config service. Mirrors <see cref="CustomThemeStore"/>: it takes a
/// single <see cref="IConfigSection"/> and round-trips primitives/arrays.
/// Pinned ids are entry titles (the launcher's stable identity).
/// </summary>
internal sealed class LauncherPrefs
{
    private const string PinnedKey = "pinned";
    private const string ModeKey = "mode";
    private const string HorizontalKey = "minimal_horizontal";

    private readonly IConfigSection _config;
    private readonly HashSet<string> _pinned = new(StringComparer.Ordinal);
    private LauncherMode _mode;
    private bool _minimalHorizontal;

    public LauncherPrefs(IConfigSection config)
    {
        _config = config;

        var raw = config.Get<string[]>(PinnedKey, null);
        if (raw is not null)
            foreach (var id in raw)
                if (!string.IsNullOrEmpty(id)) _pinned.Add(id);

        // Default = Full on first-ever open (discovery); persists thereafter.
        var modeStr = config.Get<string>(ModeKey, null);
        _mode = modeStr is not null && Enum.TryParse<LauncherMode>(modeStr, out var m)
            ? m
            : LauncherMode.Full;

        _minimalHorizontal = config.Get(HorizontalKey, false);
    }

    /// <summary>Minimal-mode orientation: false = vertical column (default), true = horizontal row.</summary>
    public bool MinimalHorizontal
    {
        get => _minimalHorizontal;
        set
        {
            if (value == _minimalHorizontal) return;
            _minimalHorizontal = value;
            _config.Set(HorizontalKey, value);
            _config.Save();
        }
    }

    public LauncherMode Mode
    {
        get => _mode;
        set
        {
            if (value == _mode) return;
            _mode = value;
            _config.Set(ModeKey, value.ToString());
            _config.Save();
        }
    }

    public bool IsPinned(string id) => _pinned.Contains(id);

    public void SetPinned(string id, bool pinned)
    {
        if (string.IsNullOrEmpty(id)) return;
        var changed = pinned ? _pinned.Add(id) : _pinned.Remove(id);
        if (!changed) return;
        Persist();
    }

    private void Persist()
    {
        var arr = new string[_pinned.Count];
        _pinned.CopyTo(arr);
        _config.Set(PinnedKey, arr);
        _config.Save();
    }
}
