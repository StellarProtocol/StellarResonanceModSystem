using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound contract for dispatching equip RPCs through the game's own
/// dispatcher. Implemented by Infrastructure
/// (<c>PandaModuleEquipProbe</c>) by invoking the game's Lua functions
/// <c>ModVM.AsyncEquipMod(uuid, slot)</c> / <c>ModVM.AsyncUninstallMod(slot)</c>
/// through the <c>ZLuaFramework</c> (<c>LuaInterface.LuaState</c>) bridge, so
/// the game runs all its own pre-flight validation.
///
/// <para><c>IsResolved</c> indicates the probe's one-time reflection
/// resolution has succeeded — i.e. the game's Lua bridge types are loaded
/// and reachable. May be false before HybridCLR finishes its 8 hot-update
/// assemblies; flips true on first successful Resolve pass.</para>
///
/// <para>Implementations also own the
/// <see cref="DrainPendingCompletions"/> tick boundary: this method
/// (called per Update tick from Bootstrap) polls the game's
/// <c>Mod.ModSlots</c> map for in-flight equips and resolves their Tasks
/// on the Update thread once the slot reflects the requested change (or
/// the 6-second timeout elapses).</para>
/// </summary>
internal interface IModuleEquipProbe
{
    bool IsResolved { get; }

    Task<EquipResult> CallInstallAsync(int slotId, long moduleUuid, CancellationToken ct);

    Task<EquipResult> CallUninstallAsync(int slotId, CancellationToken ct);

    void DrainPendingCompletions();
}
