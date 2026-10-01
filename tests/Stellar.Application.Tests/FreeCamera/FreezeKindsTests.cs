using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 3: the attr path throws MethodAccessException on scene objects, zones and collections (and toys lack the
// component statically); only moving kinds get their drawn position held.
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
    [InlineData(-1)]
    public void Attr_path_is_skipped_where_it_throws_or_the_kind_is_unknown(int kind) => Assert.False(FreezeKinds.AttrSupported(kind));

    [Fact]
    public void Only_moving_kinds_are_held()
    {
        foreach (var k in new[] { FreezeKinds.Char, FreezeKinds.Npc, FreezeKinds.Pet, FreezeKinds.Monster, FreezeKinds.Vehicle, FreezeKinds.VanityPet, FreezeKinds.Dummy })
            Assert.True(FreezeKinds.Movable(k));
        foreach (var k in new[] { FreezeKinds.SceneObject, FreezeKinds.Zone, FreezeKinds.Collection, FreezeKinds.Toy, -1 })
            Assert.False(FreezeKinds.Movable(k));
        Assert.Equal(80f, FreezeKinds.HoldRadius);
    }
}
