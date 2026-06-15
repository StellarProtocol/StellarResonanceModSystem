using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Thin pass-through implementation of <see cref="IModuleEquip"/>. The
/// async-with-event-resolution heavy lifting lives in
/// <c>PandaModuleEquipProbe</c> because it's IL2CPP-bound.
/// </summary>
internal sealed class ModuleEquipService : IModuleEquip
{
    private readonly IModuleEquipProbe _probe;

    public ModuleEquipService(IModuleEquipProbe probe)
    {
        _probe = probe;
    }

    public bool IsAvailable => _probe.IsResolved;

    public Task<EquipResult> InstallAsync(int slotId, long moduleUuid, CancellationToken ct = default)
        => _probe.CallInstallAsync(slotId, moduleUuid, ct);

    public Task<EquipResult> UninstallAsync(int slotId, CancellationToken ct = default)
        => _probe.CallUninstallAsync(slotId, ct);
}
