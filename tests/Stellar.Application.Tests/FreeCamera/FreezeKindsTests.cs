using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 3: the attr path throws MethodAccessException on scene objects, zones and collections (and toys lack the
// component statically); only moving kinds get their drawn position held.
//
// Pinned regression (in-game smoke, 2026-10-01): freezing near combat threw
// "MethodAccessException ... on type 'Panda.ZGame.BulletEnt__Storage'" because bullets/client-bullets
// (Zproto.EEntityType.EntBullet=6 / EntClientBullet=7) were not on the attr-unsupported denylist. Confirmed
// statically too: decompiling BulletEnt__Storage / ClientBulletEnt__Storage against the release_3.7 interop
// stubs shows 0 references to LocalAttrSkillStageTimeFactorComponent, same as CollectionEnt__Storage /
// ToyEnt__Storage. Do not remove Bullet/ClientBullet from AttrSupported's denylist, and do not add them to
// Movable — their visual motion is an effect (ZBulletShow via ZEffectManager.EffectDict), not a model
// position, so the effect freeze already covers them.
public sealed class FreezeKindsTests
{
    [Theory]
    [InlineData(FreezeKinds.Char)]
    [InlineData(FreezeKinds.Npc)]
    [InlineData(FreezeKinds.Pet)]
    [InlineData(FreezeKinds.Monster)]
    [InlineData(FreezeKinds.Vehicle)]
    [InlineData(FreezeKinds.VanityPet)]
    public void Attr_path_runs_on_characters_npcs_pets_monsters_and_mounts(int kind) => Assert.True(FreezeKinds.AttrSupported(kind));

    [Theory]
    [InlineData(FreezeKinds.SceneObject)]
    [InlineData(FreezeKinds.Zone)]
    [InlineData(FreezeKinds.Collection)]
    [InlineData(FreezeKinds.Toy)]
    [InlineData(FreezeKinds.Bullet)]
    [InlineData(FreezeKinds.ClientBullet)]
    [InlineData(-1)]
    public void Attr_path_is_skipped_where_it_throws_or_the_kind_is_unknown(int kind) => Assert.False(FreezeKinds.AttrSupported(kind));

    [Fact]
    public void Only_moving_kinds_are_held()
    {
        foreach (var k in new[] { FreezeKinds.Char, FreezeKinds.Npc, FreezeKinds.Pet, FreezeKinds.Monster, FreezeKinds.Vehicle, FreezeKinds.VanityPet, FreezeKinds.Dummy })
            Assert.True(FreezeKinds.Movable(k));
        foreach (var k in new[] { FreezeKinds.SceneObject, FreezeKinds.Zone, FreezeKinds.Collection, FreezeKinds.Toy, FreezeKinds.Bullet, FreezeKinds.ClientBullet, -1 })
            Assert.False(FreezeKinds.Movable(k));
        Assert.Equal(80f, FreezeKinds.HoldRadius);
    }
}
