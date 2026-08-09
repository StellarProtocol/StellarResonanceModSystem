using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IHarmonyHost"/> for one plugin. Owns every <see cref="HarmonyLib.Harmony"/> instance the
/// plugin creates and unpatches them all on <see cref="Dispose"/> (called by <c>PluginHost</c> when the
/// plugin is disposed / soft-disabled), so a plugin can never leak an un-unpatched instance.
/// </summary>
internal sealed class PerPluginHarmonyHost : IHarmonyHost, IDisposable
{
    private readonly string _pluginId;
    private readonly Action<string>? _log;
    private readonly List<HarmonyLib.Harmony> _instances = new();
    private readonly object _gate = new();
    private bool _disposed;

    public PerPluginHarmonyHost(string pluginId, Action<string>? log = null)
    {
        _pluginId = pluginId;
        _log = log;
    }

    public HarmonyLib.Harmony Create(string suffix = "")
    {
        var id = string.IsNullOrEmpty(suffix) ? _pluginId : _pluginId + "." + suffix;
        var harmony = new HarmonyLib.Harmony(id);
        lock (_gate)
        {
            if (!_disposed) _instances.Add(harmony);
        }
        return harmony;
    }

    public void Dispose()
    {
        List<HarmonyLib.Harmony> snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            snapshot = new List<HarmonyLib.Harmony>(_instances);
            _instances.Clear();
        }
        foreach (var harmony in snapshot)
        {
            try { harmony.UnpatchSelf(); }
            catch (Exception ex) { _log?.Invoke($"[HarmonyHost] unpatch '{harmony.Id}' failed: {ex.Message}"); }
        }
    }
}
