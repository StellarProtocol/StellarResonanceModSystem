namespace Stellar.Infrastructure.Game;

/// <summary>Entity kinds by the game's <c>EEntityType</c> value, read as <c>ZEntity.LuaEntType</c> (recon run 3).</summary>
internal static class FreezeKinds
{
    internal const int Monster = 1, Npc = 2, SceneObject = 3, Zone = 5, Pet = 8, Char = 10, Dummy = 11, Collection = 16,
        Vehicle = 19, Toy = 20, VanityPet = 23;

    /// <summary>Hold radius around the local player at freeze time: the camera is capped at 60 m from the player, plus
    /// what stays visible beyond that.</summary>
    internal const float HoldRadius = 80f;

    /// <summary>False where the attr path throws <c>MethodAccessException</c>: collections and toys lack
    /// <c>LocalAttrSkillStageTimeFactorComponent</c>, scene objects' recalc and zones' getter throw (run 3). Unknown
    /// kinds (−1) are skipped too; stage 2 still covers them.</summary>
    public static bool AttrSupported(int kind) => kind >= 0 && kind is not (SceneObject or Zone or Collection or Toy);

    /// <summary>Kinds that move and so get their drawn position held.</summary>
    public static bool Movable(int kind) => kind is Monster or Npc or Pet or Char or Dummy or Vehicle or VanityPet;
}
