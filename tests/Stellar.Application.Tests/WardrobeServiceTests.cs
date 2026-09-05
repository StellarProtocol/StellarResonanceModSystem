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
        public WardrobeWeaponSkin? WeaponSkin { get; set; }
        public int ApplyCode { get; set; }
        public int ApplyCalls { get; private set; }
        public int WeaponCalls { get; private set; }
        public (int professionId, int skinId)? LastWeapon { get; private set; }
        public TaskCompletionSource<int>? Gate { get; set; }

        public IReadOnlyDictionary<int, int>? ReadWorn() => Worn;

        public WardrobeWeaponSkin? ReadWornWeaponSkin() => WeaponSkin;

        public async Task<int> CallApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct)
        {
            ApplyCalls++;
            if (Gate != null) return await Gate.Task;
            return ApplyCode;
        }

        public async Task<int> CallApplyWeaponSkinAsync(int professionId, int skinId, CancellationToken ct)
        {
            WeaponCalls++;
            LastWeapon = (professionId, skinId);
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

    [Fact]
    public void GetWornWeaponSkin_passes_through_probe()
    {
        var skin = new WardrobeWeaponSkin(5, 160);
        Assert.Equal(skin, new WardrobeService(new FakeProbe { WeaponSkin = skin }).GetWornWeaponSkin());
        Assert.Null(new WardrobeService(new FakeProbe { WeaponSkin = null }).GetWornWeaponSkin());
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

    [Theory]
    [InlineData(0, WardrobeResult.Success)]
    [InlineData(7561, WardrobeResult.Rejected)]
    [InlineData(-1, WardrobeResult.Timeout)]
    [InlineData(-2, WardrobeResult.Cancelled)]
    [InlineData(-3, WardrobeResult.GameApiUnavailable)]
    public async Task ApplyWeaponSkinAsync_maps_the_probe_code_to_a_result(int code, WardrobeResult expected)
    {
        var probe = new FakeProbe { ApplyCode = code };
        var svc = new WardrobeService(probe);
        Assert.Equal(expected, await svc.ApplyWeaponSkinAsync(5, 160));
        Assert.Equal((5, 160), probe.LastWeapon);
        Assert.Equal(0, probe.ApplyCalls);   // the weapon path never sends an outfit
    }

    [Fact]
    public async Task ApplyAsync_returns_PlayerNotInWorld_when_not_in_world()
    {
        var svc = new WardrobeService(new FakeProbe { IsInWorld = false });
        Assert.Equal(WardrobeResult.PlayerNotInWorld, await svc.ApplyAsync(new Dictionary<int, int>()));
        Assert.Equal(WardrobeResult.PlayerNotInWorld, await svc.ApplyWeaponSkinAsync(5, 160));
    }

    [Fact]
    public async Task ApplyAsync_returns_GameApiUnavailable_when_unresolved()
    {
        var svc = new WardrobeService(new FakeProbe { IsResolved = false });
        Assert.Equal(WardrobeResult.GameApiUnavailable, await svc.ApplyAsync(new Dictionary<int, int>()));
        Assert.Equal(WardrobeResult.GameApiUnavailable, await svc.ApplyWeaponSkinAsync(5, 160));
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

    [Fact]
    public async Task Outfit_and_weapon_skin_share_one_in_flight_slot()
    {
        var gate = new TaskCompletionSource<int>();
        var probe = new FakeProbe { Gate = gate };
        var svc = new WardrobeService(probe);

        var outfit = svc.ApplyAsync(new Dictionary<int, int>());          // parks on the gate
        Assert.Equal(WardrobeResult.Rejected, await svc.ApplyWeaponSkinAsync(5, 160));
        Assert.Equal(0, probe.WeaponCalls);                                 // never reached the probe

        gate.SetResult(0);
        Assert.Equal(WardrobeResult.Success, await outfit);

        // Slot released → the weapon skin goes through (the plugin's await-then-send sequence).
        probe.Gate = null;
        Assert.Equal(WardrobeResult.Success, await svc.ApplyWeaponSkinAsync(5, 160));
        Assert.Equal(1, probe.WeaponCalls);
    }
}
