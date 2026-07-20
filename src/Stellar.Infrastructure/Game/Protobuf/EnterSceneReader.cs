using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Parser for WorldNtf <c>EnterScene</c> (method 3). Navigates
/// <c>EnterScene.EnterSceneInfo(1).PlayerEnt(2)</c> — the local player's full wire
/// <c>Entity</c> — and returns it via the shared <see cref="SyncNearEntitiesReader.TryReadEntity"/>
/// (uuid + AttrCollection). <c>PlayerEnt.Attrs</c> carries self's COMPLETE attr set, including the
/// equipped-skill list (<c>AttrSkillLevelIdList</c> = 116) that is absent from self's per-frame
/// <c>SyncToMeDeltaInfo</c> deltas — so this is the only path that yields the local player's Battle
/// Imagine loadout. (Mirrors BPSR-ZDPS <c>ProcessEnterScene</c>.)
/// </summary>
internal static class EnterSceneReader
{
    // EnterScene { EnterSceneInfo EnterSceneInfo = 1 }
    private const int EnterSceneInfoField = 1;
    // EnterSceneInfo { AttrCollection SceneAttrs = 1; Entity PlayerEnt = 2; string SceneGuid = 3;
    //                  string ConnectGuid = 4; AttrCollection SubsceneAttrs = 5 }
    // (field order from the IL2CPP Zproto.EnterSceneInfo generated message — same order = same field number).
    private const int SceneAttrsField = 1;
    private const int PlayerEntField  = 2;
    private const int SceneGuidField  = 3;
    private const int ConnectGuidField = 4;

    public static bool TryReadPlayerEntity(ReadOnlySpan<byte> payload, out AppearEntityMsg playerEnt)
    {
        playerEnt = default;
        // EnterScene.EnterSceneInfo = field 1; EnterSceneInfo.PlayerEnt = field 2.
        if (!TryFindField(payload, EnterSceneInfoField, out int o1, out int l1)) return false;
        var sceneInfo = payload.Slice(o1, l1);
        if (!TryFindField(sceneInfo, PlayerEntField, out int o2, out int l2)) return false;
        return SyncNearEntitiesReader.TryReadEntity(sceneInfo.Slice(o2, l2), out playerEnt);
    }

    /// <summary>
    /// Extract the server-assigned, per-run-unique scene instance id from
    /// <c>EnterScene.EnterSceneInfo(1).SceneAttrs(1)</c> — the scene-level
    /// <see cref="AttrCollectionMsg"/> — by reading the <c>AttrSceneUuid</c>
    /// (<see cref="AttrTypeIds.AttrSceneUuid"/> = 342) attr as an int64 varint.
    /// <para>
    /// This is the stable run id the dungeon-state sink consumes: it is shared
    /// by every client in the run and identical across all the per-frame
    /// dungeon syncs within one run (unlike <c>DungeonSyncData.scene_uuid</c>,
    /// which arrives through a dirty-mask container and is unreliable when read
    /// as a bare protobuf varint). Returns <see langword="false"/> (and leaves
    /// <paramref name="sceneUuid"/> 0) when the SceneAttrs sub-message is
    /// absent or carries no <c>AttrSceneUuid</c> row.
    /// </para>
    /// </summary>
    public static bool TryReadSceneId(ReadOnlySpan<byte> payload, out long sceneUuid)
    {
        sceneUuid = 0;
        if (!TryReadSceneAttrs(payload, out var sceneAttrs, out _)) return false;
        for (int i = 0; i < sceneAttrs.Items.Count; i++)
        {
            var attr = sceneAttrs.Items[i];
            if (attr.Id == AttrTypeIds.AttrSceneUuid)
            {
                sceneUuid = attr.DecodedLong;
                return sceneUuid != 0;
            }
        }
        return false;
    }

    /// <summary>
    /// Decode <c>EnterScene.EnterSceneInfo(1)</c> into its scene-level
    /// <c>SceneAttrs(1)</c> <see cref="AttrCollectionMsg"/> plus the
    /// <c>SceneGuid(3)</c> string. Exposed for the one-shot enter-scene
    /// structure diagnostic (which dumps every scene attr id/value so the run
    /// id field can be confirmed against a live run) and consumed internally by
    /// <see cref="TryReadSceneId"/>. Returns <see langword="false"/> when the
    /// EnterSceneInfo wrapper or its SceneAttrs sub-message is absent/malformed.
    /// </summary>
    public static bool TryReadSceneAttrs(
        ReadOnlySpan<byte> payload,
        out AttrCollectionMsg sceneAttrs,
        out string? sceneGuid)
    {
        sceneAttrs = default;
        sceneGuid = null;
        if (!TryFindField(payload, EnterSceneInfoField, out int o1, out int l1)) return false;
        var sceneInfo = payload.Slice(o1, l1);

        if (TryFindField(sceneInfo, SceneGuidField, out int og, out int lg))
            sceneGuid = System.Text.Encoding.UTF8.GetString(sceneInfo.Slice(og, lg));

        if (!TryFindField(sceneInfo, SceneAttrsField, out int oa, out int la)) return false;
        return AttrCollectionReader.TryRead(sceneInfo.Slice(oa, la), out sceneAttrs);
    }

    /// <summary>
    /// Reads the two string identities on <c>EnterScene.EnterSceneInfo(1)</c> —
    /// <c>SceneGuid</c> (3) and <c>ConnectGuid</c> (4). These are the client's
    /// per-connection/per-scene-server identities, present (if at all) from zone-in —
    /// unlike <c>AttrSceneUuid</c> (342), which stays a persistent field-class id until
    /// the run officially starts. Diagnostic surface only (spec 2026-07-19 § 8.3):
    /// neither can key uploads (<c>level_uuid</c> must be the shared snowflake).
    /// Returns <see langword="false"/> when neither field is present.
    /// </summary>
    public static bool TryReadSceneGuids(ReadOnlySpan<byte> payload, out string sceneGuid, out string connectGuid)
    {
        sceneGuid = "";
        connectGuid = "";
        if (!TryFindField(payload, EnterSceneInfoField, out int o1, out int l1)) return false;
        var sceneInfo = payload.Slice(o1, l1);
        if (TryFindField(sceneInfo, SceneGuidField, out int o2, out int l2))
            sceneGuid = System.Text.Encoding.UTF8.GetString(sceneInfo.Slice(o2, l2));
        if (TryFindField(sceneInfo, ConnectGuidField, out int o3, out int l3))
            connectGuid = System.Text.Encoding.UTF8.GetString(sceneInfo.Slice(o3, l3));
        return sceneGuid.Length > 0 || connectGuid.Length > 0;
    }

    // Locate the first length-delimited (wire-type 2) field == targetField; return its payload offset+length
    // (ints, not spans — avoids ref-escape so the caller slices it safely).
    private static bool TryFindField(ReadOnlySpan<byte> payload, int targetField, out int offset, out int length)
    {
        offset = 0; length = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            if (field == targetField && wire == 2)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var len)) return false;
                offset = pos; length = (int)len;
                return length >= 0 && offset + length <= payload.Length;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) return false;
        }
        return false;
    }
}
