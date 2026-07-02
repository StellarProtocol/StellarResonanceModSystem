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
/// HEURISTIC, not a classification: if a genuinely-wanted world boss / raid ever
/// presents a below-floor uuid (or a non-dungeon scene ever exceeds the floor),
/// replace this magnitude gate with a real scene classification via
/// <c>AttrSceneBasicId</c> (341) against the game-data scene/dungeon tables.
/// </para>
/// </summary>
internal static class DungeonRunIdGate
{
    /// <summary>
    /// Floor separating instanced-scene snowflakes from persistent town/home/
    /// open-world scene ids. 2^53 = 9007199254740992.
    /// </summary>
    public const long DungeonInstanceUuidFloor = 1L << 53;

    /// <summary>
    /// The run id for a scene whose <c>AttrSceneUuid</c> is <paramref name="sceneUuid"/>:
    /// the snowflake itself for instanced content, or 0 for non-instanced scenes.
    /// </summary>
    public static long Resolve(long sceneUuid)
        => sceneUuid > DungeonInstanceUuidFloor ? sceneUuid : 0L;
}
