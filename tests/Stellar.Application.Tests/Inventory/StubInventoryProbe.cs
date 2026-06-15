using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.Inventory;

internal sealed class StubInventoryProbe : IInventoryProbe
{
    public ModuleSnapshot? NextModules { get; set; }
    public EquippedSet? NextEquipped { get; set; }
    public bool ModulesReadable { get; set; } = true;
    public bool EquippedReadable { get; set; } = true;
    public int ModulesCallCount { get; private set; }
    public int EquippedCallCount { get; private set; }

    public bool TryReadModules(out ModuleSnapshot snapshot)
    {
        ModulesCallCount++;
        if (!ModulesReadable || NextModules is null)
        {
            snapshot = default!;
            return false;
        }
        snapshot = NextModules;
        return true;
    }

    public bool TryReadEquipped(out EquippedSet equipped)
    {
        EquippedCallCount++;
        if (!EquippedReadable || NextEquipped is null)
        {
            equipped = default!;
            return false;
        }
        equipped = NextEquipped;
        return true;
    }

    public static ModuleSnapshot SnapshotOf(params (long uuid, int configId)[] modules)
    {
        var list = new List<ModuleInfo>(modules.Length);
        foreach (var (uuid, configId) in modules)
        {
            list.Add(new ModuleInfo(uuid, configId, $"Mod#{configId}", 4, ModuleCategory.Attack,
                new List<ModulePart> { new ModulePart(11011, "Strength", 4) }));
        }
        return new ModuleSnapshot(list, 0);
    }

    public static EquippedSet EquippedOf(params (int slot, long uuid)[] slots)
    {
        var d = new Dictionary<int, long>(slots.Length);
        foreach (var (s, u) in slots) d[s] = u;
        return new EquippedSet(d);
    }
}
