namespace Stellar.Infrastructure.Game;

/// <summary>Entity kinds by the game's <c>EEntityType</c> value, read as <c>ZEntity.LuaEntType</c> (recon run 3).
/// <c>Bullet</c>/<c>ClientBullet</c> values are <c>Zproto.EEntityType.EntBullet</c> / <c>EntClientBullet</c>
/// (`Panda.ZRpcGen.dll`, decompiled with ilspycmd against the release_3.7 interop stubs) — every other kind here
/// already matches that enum 1:1 by name (<c>EntMonster</c>=1 … <c>EntVanityPet</c>=23), and <c>BulletEnt</c> /
/// <c>ClientBulletEnt</c> follow the same convention.</summary>
internal static class FreezeKinds
{
    internal const int Monster = 1, Npc = 2, SceneObject = 3, Zone = 5, Bullet = 6, ClientBullet = 7, Pet = 8, Char = 10,
        Dummy = 11, Collection = 16, Vehicle = 19, Toy = 20, VanityPet = 23;

    /// <summary>Hold radius around the local player at freeze time: the camera is capped at 60 m from the player, plus
    /// what stays visible beyond that.</summary>
    internal const float HoldRadius = 80f;

    /// <summary>False where the attr path throws <c>MethodAccessException</c>: collections and toys lack
    /// <c>LocalAttrSkillStageTimeFactorComponent</c>, scene objects' recalc and zones' getter throw (run 3). Bullets
    /// lack the component too — confirmed both by an in-game smoke (`MethodAccessException … on type
    /// 'Panda.ZGame.BulletEnt__Storage'`) and statically (decompiling `BulletEnt__Storage` /
    /// `ClientBulletEnt__Storage` against the release_3.7 interop stubs: 0 references to
    /// <c>LocalAttrSkillStageTimeFactorComponent</c>, same as <c>CollectionEnt__Storage</c>/<c>ToyEnt__Storage</c>).
    /// A bullet's visual motion is already covered by the effect freeze — bullets are a "show" entity
    /// (<c>ZBulletShow</c>) driven through <c>ZEffectManager.EffectDict</c>, not a model position, so they get no
    /// position hold (<see cref="Movable"/> below; the freeze engine's effect pass already stops them). Unknown
    /// kinds (−1) are skipped too; stage 2 still covers them.</summary>
    public static bool AttrSupported(int kind) => kind >= 0 && kind is not (SceneObject or Zone or Collection or Toy or Bullet or ClientBullet);

    /// <summary>Kinds that move and so get their drawn position held.</summary>
    public static bool Movable(int kind) => kind is Monster or Npc or Pet or Char or Dummy or Vehicle or VanityPet;
}
