using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// <see cref="CombatService"/>'s buff effect-id side table (ICombatBuffEffectLookup): keyed by (uuid &gt;&gt; 16,
/// BuffUuid), last update wins, null ids remove, scene change (<see cref="CombatService.ClearAllBuffs"/>) clears.
/// </summary>
public sealed class CombatServiceBuffEffectIdsTests
{
    private static readonly EntityId Player = new((1248014L << 16) | 640L);

    private static CombatService Make() =>
        new(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), new StubSocialRefreshRequester());

    [Fact]
    public void Apply_ThenLookup_LastUpdateWins()
    {
        var svc = Make();
        svc.ApplyBuffEffectIds(Player, new[]
        {
            new BuffEffectIdUpdate(0, 100, new[] { 1 }),
            new BuffEffectIdUpdate(0, 100, new[] { 2, 3 }),
        });

        Assert.True(svc.TryGetBuffEffectIds(Player, 100, out var ids));
        Assert.Equal(new[] { 2, 3 }, ids);
        Assert.False(svc.TryGetBuffEffectIds(Player, 101, out var none));
        Assert.Empty(none);
    }

    [Fact]
    public void Key_IsEntityPartOfUuid_HostUuidOverridesMessageEntity()
    {
        var svc = Make();
        var other = new EntityId((55L << 16) | 640L);
        // A wire HostUuid naming another entity; the local player's client-flagged uuid differs only in the low bits.
        svc.ApplyBuffEffectIds(Player, new[] { new BuffEffectIdUpdate(other.Value, 7, new[] { 9 }) });

        Assert.False(svc.TryGetBuffEffectIds(Player, 7, out _));
        Assert.True(svc.TryGetBuffEffectIds(new EntityId((55L << 16) | 0x281), 7, out var ids));
        Assert.Equal(new[] { 9 }, ids);
    }

    [Fact]
    public void NullIds_Remove_And_ClearAllBuffs_Clears()
    {
        var svc = Make();
        svc.ApplyBuffEffectIds(Player, new[] { new BuffEffectIdUpdate(0, 1, new[] { 4 }), new BuffEffectIdUpdate(0, 2, new[] { 5 }) });
        svc.ApplyBuffEffectIds(Player, new[] { new BuffEffectIdUpdate(0, 1, null) });

        Assert.False(svc.TryGetBuffEffectIds(Player, 1, out _));
        Assert.True(svc.TryGetBuffEffectIds(Player, 2, out _));

        svc.ClearAllBuffs();
        Assert.False(svc.TryGetBuffEffectIds(Player, 2, out _));
    }
}
