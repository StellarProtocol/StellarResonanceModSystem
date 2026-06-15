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
    public static bool TryReadPlayerEntity(ReadOnlySpan<byte> payload, out AppearEntityMsg playerEnt)
    {
        playerEnt = default;
        // EnterScene.EnterSceneInfo = field 1; EnterSceneInfo.PlayerEnt = field 2.
        if (!TryFindField(payload, 1, out int o1, out int l1)) return false;
        var sceneInfo = payload.Slice(o1, l1);
        if (!TryFindField(sceneInfo, 2, out int o2, out int l2)) return false;
        return SyncNearEntitiesReader.TryReadEntity(sceneInfo.Slice(o2, l2), out playerEnt);
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
