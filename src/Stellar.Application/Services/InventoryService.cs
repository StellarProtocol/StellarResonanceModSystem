using System;
using System.Collections.Generic;
using System.Threading;
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

    private ModuleSnapshot? _modules;
    private EquippedSet? _equipped;
    private long _lastHash;

    public InventoryService(IInventoryProbe probe, SelfGearCache selfGear, IPluginLog log)
    {
        _probe = probe;
        _selfGear = selfGear;
        _log = log;
    }

    public bool IsAvailable => Volatile.Read(ref _modules) is not null;

    public ModuleSnapshot? GetModules() => Volatile.Read(ref _modules);

    public EquippedSet? GetEquipped() => Volatile.Read(ref _equipped);

    // Self gear is push-fed (method-21 full sync → SelfGearCache), not part of
    // the 1Hz probe poll — serve it straight off the volatile-swap cache.
    public IReadOnlyList<GearInstance> GetSelfGear() => _selfGear.Current;

    public event Action? InventoryChanged;

    /// <summary>Called at 1Hz from BootstrapPlugin. Reads from the probe,
    /// computes a hash of the inventory + equipped state, fires the event
    /// only when the hash changes.</summary>
    internal void Refresh()
    {
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
