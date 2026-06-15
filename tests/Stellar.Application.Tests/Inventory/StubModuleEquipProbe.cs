using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.Inventory;

internal sealed class StubModuleEquipProbe : IModuleEquipProbe
{
    public bool IsResolved { get; set; } = true;

    public EquipResult NextInstallResult { get; set; } = EquipResult.Success;
    public EquipResult NextUninstallResult { get; set; } = EquipResult.Success;
    public int InstallCallCount { get; private set; }
    public int UninstallCallCount { get; private set; }
    public int LastSlotId { get; private set; }
    public long LastModuleUuid { get; private set; }
    public int DrainCount { get; private set; }

    public Task<EquipResult> CallInstallAsync(int slotId, long moduleUuid, CancellationToken ct)
    {
        InstallCallCount++;
        LastSlotId = slotId;
        LastModuleUuid = moduleUuid;
        return ct.IsCancellationRequested
            ? Task.FromResult(EquipResult.Cancelled)
            : Task.FromResult(NextInstallResult);
    }

    public Task<EquipResult> CallUninstallAsync(int slotId, CancellationToken ct)
    {
        UninstallCallCount++;
        LastSlotId = slotId;
        return ct.IsCancellationRequested
            ? Task.FromResult(EquipResult.Cancelled)
            : Task.FromResult(NextUninstallResult);
    }

    public void DrainPendingCompletions() => DrainCount++;
}
