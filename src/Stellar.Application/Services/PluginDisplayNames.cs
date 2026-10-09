using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Resolves a plugin's display name for Settings from the FIRST launcher entry that plugin registered
/// (<see cref="LauncherEntry.DisplayTitle"/> — the plugin's own live-localized tile title), else its internal
/// name. Owner attribution comes from <see cref="ILauncherOwnedRegistrations"/> (recorded by
/// <c>PerPluginLauncher</c> at Register), so another plugin's entries never leak in. Evaluated on demand by the
/// panels' row Funcs (render-driven: <c>WindowService</c> pulls nothing from a hidden window) — never call it from
/// a per-tick path. A throwing <c>TitleProvider</c> is fail-safe like <c>WindowService.SafeApply</c> / the launcher
/// pin migration: fall back to the internal name, log once, and stop invoking that plugin's provider.
/// </summary>
internal sealed class PluginDisplayNames : IPluginDisplayNames
{
    private readonly ILauncherOwnedRegistrations _launcher;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);   // threw once → skipped + logged once

    public PluginDisplayNames(ILauncherOwnedRegistrations launcher, IPluginLog log)
    {
        _launcher = launcher;
        _log = log;
    }

    public string Resolve(string pluginId, string fallback)
    {
        // A provider that threw once is skipped from then on (row Funcs re-resolve every apply — an exception per
        // frame is a real cost). The same set gates the log-once.
        if (_failed.Contains(pluginId)) return fallback;
        if (_launcher.FirstEntryOwnedBy(pluginId) is not { } entry) return fallback;
        try
        {
            var title = entry.DisplayTitle;   // runs the plugin's TitleProvider (its own T() lookup)
            return string.IsNullOrWhiteSpace(title) ? fallback : title;
        }
        catch (Exception ex)
        {
            if (_failed.Add(pluginId))
                _log.Warning($"[Settings] plugin '{pluginId}' launcher TitleProvider threw; showing its internal name: {ex.Message}");
            return fallback;
        }
    }
}
