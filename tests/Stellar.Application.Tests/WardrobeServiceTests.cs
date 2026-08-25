using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class WardrobeServiceTests
{
    private sealed class FakeProbe : IWardrobeProbe
    {
        public bool IsResolved { get; set; } = true;
        public bool IsInWorld { get; set; } = true;
        public IReadOnlyDictionary<int, int>? Worn { get; set; }
        public int ApplyCode { get; set; }
        public int ApplyCalls { get; private set; }
        public TaskCompletionSource<int>? Gate { get; set; }

        public IReadOnlyDictionary<int, int>? ReadWorn() => Worn;

        public async Task<int> CallApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct)
        {
            ApplyCalls++;
            if (Gate != null) return await Gate.Task;
            return ApplyCode;
        }
    }

    [Fact]
    public void IsAvailable_requires_resolved_and_in_world()
    {
        Assert.False(new WardrobeService(new FakeProbe { IsResolved = true, IsInWorld = false }).IsAvailable);
        Assert.False(new WardrobeService(new FakeProbe { IsResolved = false, IsInWorld = true }).IsAvailable);
        Assert.True(new WardrobeService(new FakeProbe { IsResolved = true, IsInWorld = true }).IsAvailable);
    }

    [Fact]
    public void GetWornOutfit_passes_through_probe()
    {
        var worn = new Dictionary<int, int> { [701] = 5, [702] = 0 };
        var svc = new WardrobeService(new FakeProbe { Worn = worn });
        Assert.Equal(worn, svc.GetWornOutfit());
    }

    [Theory]
    [InlineData(0, WardrobeResult.Success)]
    [InlineData(7561, WardrobeResult.Rejected)]
    [InlineData(-1, WardrobeResult.Timeout)]
    [InlineData(-2, WardrobeResult.Cancelled)]
    [InlineData(-3, WardrobeResult.GameApiUnavailable)]
    public async Task ApplyAsync_maps_the_probe_code_to_a_result(int code, WardrobeResult expected)
    {
        var svc = new WardrobeService(new FakeProbe { ApplyCode = code });
        Assert.Equal(expected, await svc.ApplyAsync(new Dictionary<int, int>()));
    }

    [Fact]
    public async Task ApplyAsync_returns_PlayerNotInWorld_when_not_in_world()
    {
        var svc = new WardrobeService(new FakeProbe { IsInWorld = false });
        Assert.Equal(WardrobeResult.PlayerNotInWorld, await svc.ApplyAsync(new Dictionary<int, int>()));
    }

    [Fact]
    public async Task ApplyAsync_returns_GameApiUnavailable_when_unresolved()
    {
        var svc = new WardrobeService(new FakeProbe { IsResolved = false });
        Assert.Equal(WardrobeResult.GameApiUnavailable, await svc.ApplyAsync(new Dictionary<int, int>()));
    }

    [Fact]
    public async Task ApplyAsync_rejects_a_second_concurrent_apply()
    {
        var gate = new TaskCompletionSource<int>();
        var probe = new FakeProbe { Gate = gate };
        var svc = new WardrobeService(probe);

        var first = svc.ApplyAsync(new Dictionary<int, int>());   // parks on the gate
        var second = await svc.ApplyAsync(new Dictionary<int, int>());
        Assert.Equal(WardrobeResult.Rejected, second);
        Assert.Equal(1, probe.ApplyCalls);

        gate.SetResult(0);
        Assert.Equal(WardrobeResult.Success, await first);
    }
}
