using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Soft-cycle plugin lifecycle. Each registered plugin has a factory delegate
/// stored alongside its <see cref="PluginInfo"/>; <see cref="SetEnabled"/> with
/// false disposes the instance, with true reconstructs from the same factory.
/// Disabled-state persists via the framework's <c>plugins</c> section so
/// toggles survive restart.
/// </summary>
internal sealed class PluginRegistry : IPluginInventory, IPluginManagement
{
    private const string DisabledIdsKey = "disabled_ids";

    private readonly Dictionary<string, PluginSlot> _slots = new();
    private readonly IConfigSection _config;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _disabledIds;
    private readonly IPluginServices _services;
    // Cached IReadOnlyList<PluginInfo> returned from IPluginInventory.List() —
    // PluginsPanel.DrawBody calls List() every OnGUI pass; the previous impl
    // allocated a fresh List each call. Invalidated on every StatusChanged
    // (Register / SetEnabled / RequestRetry).
    private IReadOnlyList<PluginInfo>? _cachedList;

    public PluginRegistry(IConfigSection config, IPluginLog log, IPluginServices services)
    {
        _config = config;
        _log = log;
        _services = services;
        _disabledIds = LoadDisabledIds();
    }

    public event Action<PluginInfo>? StatusChanged;

    public IReadOnlyList<PluginInfo> List()
    {
        if (_cachedList is not null) return _cachedList;
        var result = new List<PluginInfo>(_slots.Count);
        foreach (var slot in _slots.Values) result.Add(slot.Info);
        _cachedList = result;
        return result;
    }

    private void InvalidateListCache() => _cachedList = null;

    /// <summary>
    /// Register a plugin. If its id is in the persisted disabled list the slot
    /// is created in the disabled state (factory captured but not invoked);
    /// otherwise the factory runs immediately and the instance is stored.
    /// </summary>
    public void Register(string id, string displayName, string version,
                          Func<IPluginServices, object> factory)
    {
        if (_slots.ContainsKey(id))
        {
            _log.Error($"[PluginRegistry] duplicate plugin id '{id}'; second registration ignored.");
            return;
        }
        var info = new PluginInfo(id, displayName, version, IsEnabled: false, IsErrored: false);
        _slots[id] = new PluginSlot(info, factory, instance: null);

        if (_disabledIds.Contains(id))
        {
            _log.Info($"[PluginRegistry] registered '{id}' (disabled per config)");
            InvalidateListCache();
            StatusChanged?.Invoke(_slots[id].Info);
            return;
        }
        EnableInternal(_slots[id]);
        InvalidateListCache();
        StatusChanged?.Invoke(_slots[id].Info);
    }

    public void SetEnabled(string pluginId, bool enabled)
    {
        if (!_slots.TryGetValue(pluginId, out var slot))
        {
            _log.Warning($"[PluginRegistry] SetEnabled: unknown id '{pluginId}'");
            return;
        }
        if (slot.Info.IsEnabled == enabled && !slot.Info.IsErrored) return;

        if (enabled) EnableInternal(slot);
        else DisableInternal(slot);
        PersistDisabledIds();
        InvalidateListCache();
        StatusChanged?.Invoke(slot.Info);
    }

    public void RequestRetry(string pluginId)
    {
        if (!_slots.TryGetValue(pluginId, out var slot)) return;
        if (!slot.Info.IsErrored) return;
        slot.Info = slot.Info with { IsErrored = false, LastErrorMessage = null };
        // Treat retry as "ask for enable" — if the plugin was last in the
        // enabled-but-errored state, this re-runs the factory. If it was
        // disabled-and-errored (rare), the user must Enable explicitly.
        if (!_disabledIds.Contains(pluginId))
            EnableInternal(slot);
        InvalidateListCache();
        StatusChanged?.Invoke(slot.Info);
    }

    IReadOnlyList<PluginStatus> IPluginManagement.List()
    {
        var result = new List<PluginStatus>(_slots.Count);
        foreach (var s in _slots.Values)
            result.Add(new PluginStatus(s.Info.Id, s.Info.DisplayName, s.Info.Version, s.Info.IsEnabled, s.Info.IsErrored));
        return result;
    }

    void IPluginManagement.SetEnabled(string pluginId, bool enabled) => SetEnabled(pluginId, enabled);

    /// <summary>
    /// Dispose every live instance — called on host shutdown so plugin
    /// Dispose contracts (event detach, HarmonyX unpatch, Unity destroy)
    /// run exactly once during framework teardown.
    /// </summary>
    public void DisposeAll()
    {
        foreach (var slot in _slots.Values)
        {
            if (slot.Instance is null) continue;
            try { (slot.Instance as IDisposable)?.Dispose(); }
            catch (Exception ex) { _log.Warning($"[PluginRegistry] Dispose threw for '{slot.Info.Id}': {ex.GetType().Name}: {ex.Message}"); }
            slot.Instance = null;
        }
    }

    private void EnableInternal(PluginSlot slot)
    {
        try
        {
            slot.Instance = slot.Factory(_services);
            slot.Info = slot.Info with { IsEnabled = true, IsErrored = false, LastErrorMessage = null };
            _disabledIds.Remove(slot.Info.Id);
            // Distinguish first construction from a soft-cycle reconstruction
            // so post-mortem log diffs can tell the two apart. The slot's
            // WasConstructedBefore flag is set AFTER the success log emits.
            var verb = slot.WasConstructedBefore ? "reconstructed" : "constructed";
            _log.Info($"[PluginRegistry] enabled '{slot.Info.Id}' ({verb})");
            slot.WasConstructedBefore = true;
        }
        catch (Exception ex)
        {
            slot.Instance = null;
            slot.Info = slot.Info with { IsEnabled = false, IsErrored = true, LastErrorMessage = ex.Message };
            _log.Error($"[PluginRegistry] enable failed for '{slot.Info.Id}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void DisableInternal(PluginSlot slot)
    {
        try { (slot.Instance as IDisposable)?.Dispose(); }
        catch (Exception ex) { _log.Warning($"[PluginRegistry] Dispose threw for '{slot.Info.Id}': {ex.GetType().Name}: {ex.Message}"); }
        slot.Instance = null;
        slot.Info = slot.Info with { IsEnabled = false };
        _disabledIds.Add(slot.Info.Id);
        _log.Info($"[PluginRegistry] disabled '{slot.Info.Id}' (Disposed)");
    }

    private HashSet<string> LoadDisabledIds()
    {
        // IConfigSection's Get<T> handles arrays of primitives — Set<string[]>
        // round-trips through JsonSerializer cleanly.
        var arr = _config.Get<string[]>(DisabledIdsKey, Array.Empty<string>()) ?? Array.Empty<string>();
        return new HashSet<string>(arr, StringComparer.Ordinal);
    }

    private void PersistDisabledIds()
    {
        var arr = new string[_disabledIds.Count];
        _disabledIds.CopyTo(arr);
        _config.Set(DisabledIdsKey, arr);
        _config.Save();
    }

    private sealed class PluginSlot
    {
        public PluginSlot(PluginInfo info, Func<IPluginServices, object> factory, object? instance)
        {
            Info = info;
            Factory = factory;
            Instance = instance;
        }
        public PluginInfo Info { get; set; }
        public Func<IPluginServices, object> Factory { get; }
        public object? Instance { get; set; }
        // Flips true after the first successful Factory invocation so the
        // log message can distinguish the original Enable from a soft-cycle
        // re-enable (Mn10).
        public bool WasConstructedBefore { get; set; }
    }
}
