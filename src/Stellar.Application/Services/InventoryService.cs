using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Implementation of <see cref="IInventory"/>. Polled at 1Hz by
/// <c>BootstrapPlugin.RefreshPerTickServices</c>; hash-diff suppresses
/// redundant <see cref="InventoryChanged"/> fires.
/// </summary>
internal sealed class InventoryService : IInventory
{
    private readonly IInventoryProbe _probe;
    private readonly SelfGearCache _selfGear;
    private readonly IPluginLog _log;
    private readonly IClientState _clientState;

    private ModuleSnapshot? _modules;
    private EquippedSet? _equipped;
    private long _lastHash;

    public InventoryService(IInventoryProbe probe, SelfGearCache selfGear, IPluginLog log, IClientState clientState)
    {
        _probe = probe;
        _selfGear = selfGear;
        _log = log;
        _clientState = clientState;
        _selfGear.Changed += RaiseSelfGearChanged;
    }

    public bool IsAvailable => Volatile.Read(ref _modules) is not null;

    public ModuleSnapshot? GetModules() => Volatile.Read(ref _modules);

    public EquippedSet? GetEquipped() => Volatile.Read(ref _equipped);

    // Self gear is push-fed (method-21 full sync → SelfGearCache), not part of
    // the 1Hz probe poll — serve it straight off the volatile-swap cache.
    public IReadOnlyList<GearInstance> GetSelfGear() => _selfGear.Current;

    // Live equipped set straight from the containers (reflects manual edits / class-swap re-equips) —
    // a fresh reflection read, not the 1Hz-polled snapshot. Consumers re-read this on SelfGearChanged.
    public EquippedLoadout GetLiveEquipped() => _probe.GetLiveEquipped();

    public event Action? InventoryChanged;

    public event Action? SelfGearChanged;

    // Forwards SelfGearCache.Changed (network/sync thread) to plugin subscribers. Swallows subscriber
    // exceptions like the InventoryChanged path so one bad handler can't tear down the sync thread.
    private void RaiseSelfGearChanged()
    {
        try
        {
            SelfGearChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][Inventory] self-gear subscriber threw: {ex.Message}");
        }
    }

    /// <summary>Called at 1Hz from BootstrapPlugin. Reads from the probe,
    /// computes a hash of the inventory + equipped state, fires the event
    /// only when the hash changes.</summary>
    [WorldGated]
    internal void Refresh()
    {
        if (!_clientState.IsWorldActive) return;
        if (!_probe.TryReadModules(out var snap)) return;
        if (!_probe.TryReadEquipped(out var eq)) return;

        var newHash = ComputeHash(snap, eq);
        if (newHash == _lastHash && _modules is not null) return;

        Volatile.Write(ref _modules, snap);
        Volatile.Write(ref _equipped, eq);
        _lastHash = newHash;

        try
        {
            InventoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][Inventory] subscriber threw: {ex.Message}");
        }
    }

    /// <summary>Clear account/character-scoped inventory state on logout. Nulls the polled
    /// module/equipped snapshots (so <see cref="IsAvailable"/> reads false) and empties the self-gear
    /// cache. <see cref="Refresh"/> is <c>[WorldGated]</c> so it will NOT self-clear once the world
    /// goes inactive. Does NOT fire InventoryChanged / SelfGearChanged — a logout is teardown, not a
    /// live inventory edit. Called by the Host OnLogout dispatcher.</summary>
    internal void ClearSession()
    {
        Volatile.Write(ref _modules, null);
        Volatile.Write(ref _equipped, null);
        _lastHash = 0;
        _selfGear.ClearSession();
    }

    // Order-invariant cheap hash: each (uuid, configId) for inventory;
    // (slot, uuid) for equipped. Sufficient to detect any meaningful diff
    // at 1Hz cadence.
    private static long ComputeHash(ModuleSnapshot snap, EquippedSet eq)
    {
        long h = 0;
        foreach (var m in snap.Modules)
        {
            h ^= unchecked(m.Uuid * 31 + m.ConfigId);
        }
        foreach (var kv in eq.ModuleUuidsBySlot)
        {
            h ^= unchecked(((long)kv.Key) * 0xDEADBEEFL + kv.Value);
        }
        return h;
    }
}
