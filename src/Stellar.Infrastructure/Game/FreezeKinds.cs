namespace Stellar.Infrastructure.Game;

/// <summary>Entity kinds by the game's <c>EEntityType</c> value, read as <c>ZEntity.LuaEntType</c> (recon run 3).
/// <c>Bullet</c>/<c>ClientBullet</c> values are <c>Zproto.EEntityType.EntBullet</c> / <c>EntClientBullet</c>
/// (`Panda.ZRpcGen.dll`, decompiled with ilspycmd against the release_3.7 interop stubs) — every other kind here
/// already matches that enum 1:1 by name (<c>EntMonster</c>=1 … <c>EntVanityPet</c>=23), and <c>BulletEnt</c> /
/// <c>ClientBulletEnt</c> follow the same convention. Bullets are a "show" entity driven through the effect manager, not a
/// model position, so they get no position hold (<see cref="Movable"/>); the time pause stops them.</summary>
internal static class FreezeKinds
{
    internal const int Monster = 1, Npc = 2, SceneObject = 3, Zone = 5, Bullet = 6, ClientBullet = 7, Pet = 8, Char = 10,
        Dummy = 11, Collection = 16, Vehicle = 19, Toy = 20, VanityPet = 23;

    /// <summary>Hold radius around the local player at freeze time: the camera is capped at 60 m from the player, plus
    /// what stays visible beyond that.</summary>
    internal const float HoldRadius = 80f;

    /// <summary>Kinds that move and so get their drawn position held.</summary>
    public static bool Movable(int kind) => kind is Monster or Npc or Pet or Char or Dummy or Vehicle or VanityPet;
}
