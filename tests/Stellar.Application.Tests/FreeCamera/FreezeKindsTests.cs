using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 3: only moving kinds get their drawn position held. Pinned regression (in-game smoke, 2026-10-01): bullets /
// client-bullets (Zproto.EEntityType.EntBullet=6 / EntClientBullet=7) are never Movable — their visual motion is an effect
// (ZBulletShow), not a model position; the time pause stops them (2026-10-02 late). The attr-path denylist
// (AttrSupported) went with the per-entity freeze: nothing writes the attr path any more.
public sealed class FreezeKindsTests
{
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
