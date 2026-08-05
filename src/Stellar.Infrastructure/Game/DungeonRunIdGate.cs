namespace Stellar.Infrastructure.Game;

/// <summary>
/// Maps an enter-scene's server-assigned <c>AttrSceneUuid</c> (342) to the run id
/// the dungeon-state sink should hold.
///
/// <para>
/// Instanced content — dungeons, instanced world bosses, and raids — carries a
/// server snowflake scene uuid far above <see cref="DungeonInstanceUuidFloor"/>
/// (observed ~1e17–1e18, all above 2^53); town/home/open-world FIELD scenes carry
/// small persistent ids below it (e.g. 281874408669184 ≈ 2.8e14). So an
/// above-floor uuid IS the run id; anything else maps to 0 ("no run").
/// </para>
///
/// <para>
/// Returning 0 for non-instanced scenes (rather than leaving the previous id in
/// place) is the fix for the run-identity collision: without it, the last
/// dungeon's id lingered across every subsequent town/field scene until logout,
/// so an open-world fight archived under the previous dungeon's id and two
/// distinct runs shared one <c>level_uuid</c>. Clearing to 0 makes an open-world
/// run carry no id, so the upload/replay plugin (which refuses id 0) simply
/// doesn't upload it — logging is restricted to instanced content, as intended.
/// </para>
///
/// <para>
/// The magnitude test is only a FALLBACK now. The real classification is the
/// scene table's <c>SceneType</c> (<see cref="Stellar.Abstractions.Domain.GameData.SceneInfo.SceneKind"/>,
/// resolved from <c>AttrSceneBasicId</c> 341 via <c>IGameDataWorld.GetScene</c>):
/// <see cref="SceneKindInstanced"/> = instanced dungeon/raid, 1 = world/town/field.
/// When the kind is KNOWN it wins over magnitude — a below-floor uuid on an
/// instanced scene IS still the run id (the fix for ranked content, e.g. Mistveil
/// Hunting Ground, whose per-instance uuid sometimes lands below 2^53), and a
/// known non-instanced scene is 0 even above the floor. The magnitude gate is used
/// only when the kind is unknown (game data not loaded yet / scene absent from the
/// table), so behaviour never regresses when classification data is unavailable.
/// </para>
/// </summary>
internal static class DungeonRunIdGate
{
    /// <summary>
    /// Floor separating instanced-scene snowflakes from persistent town/home/
    /// open-world scene ids. 2^53 = 9007199254740992. Used only as the fallback
    /// when the scene kind is unknown.
    /// </summary>
    public const long DungeonInstanceUuidFloor = 1L << 53;

    /// <summary>
    /// <c>SceneTable.SceneType</c> value for instanced dungeon/raid content — the
    /// only kind that carries a run id. (1 = world/town/field; 0 = login/memory.)
    /// </summary>
    public const int SceneKindInstanced = 2;

    /// <summary>
    /// The run id for a scene whose per-instance <c>AttrSceneUuid</c> is
    /// <paramref name="sceneUuid"/> and whose scene-table <c>SceneType</c> is
    /// <paramref name="sceneKind"/> (null when unknown). Instanced → the uuid
    /// itself (even below the floor); any other KNOWN kind → 0; unknown → the
    /// magnitude fallback.
    /// </summary>
    public static long Resolve(long sceneUuid, int? sceneKind = null)
    {
        if (sceneKind == SceneKindInstanced) return sceneUuid;   // instanced — the run id, regardless of magnitude
        if (sceneKind is not null) return 0L;                    // known non-instanced (field/town/login/memory)
        return sceneUuid > DungeonInstanceUuidFloor ? sceneUuid : 0L;   // unknown kind → magnitude fallback
    }
}
