using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

public class ModuleEquipServiceTests
{
    [Fact]
    public async Task InstallAsync_PassesThrough_AndReturnsProbeResult()
    {
        var probe = new StubModuleEquipProbe { NextInstallResult = EquipResult.Success };
        var svc = new ModuleEquipService(probe);

        var result = await svc.InstallAsync(2, 12345L);

        Assert.Equal(EquipResult.Success, result);
        Assert.Equal(1, probe.InstallCallCount);
        Assert.Equal(2, probe.LastSlotId);
        Assert.Equal(12345L, probe.LastModuleUuid);
    }

    [Fact]
    public async Task InstallAsync_PropagatesCancellation()
    {
        var probe = new StubModuleEquipProbe();
        var svc = new ModuleEquipService(probe);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await svc.InstallAsync(1, 1L, cts.Token);

        Assert.Equal(EquipResult.Cancelled, result);
    }

    [Fact]
    public async Task UninstallAsync_PassesThrough()
    {
        var probe = new StubModuleEquipProbe { NextUninstallResult = EquipResult.SlotEmpty };
        var svc = new ModuleEquipService(probe);

        var result = await svc.UninstallAsync(3);

        Assert.Equal(EquipResult.SlotEmpty, result);
        Assert.Equal(1, probe.UninstallCallCount);
        Assert.Equal(3, probe.LastSlotId);
    }

    [Fact]
    public void IsAvailable_TracksProbeResolved()
    {
        var probe = new StubModuleEquipProbe { IsResolved = false };
        var svc = new ModuleEquipService(probe);

        Assert.False(svc.IsAvailable);

        probe.IsResolved = true;
        Assert.True(svc.IsAvailable);
    }
}
