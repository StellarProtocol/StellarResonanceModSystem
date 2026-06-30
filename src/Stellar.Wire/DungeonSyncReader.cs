using System;

namespace Stellar.Wire;

/// <summary>
/// Decoded result of a <c>WorldNtf.SyncDungeonData</c> structural parse. A
/// value with <see cref="SceneUuid"/> != 0 is the per-run unique id that the
/// StellarLogs upload plugin consumes as <c>level_uuid</c>. When
/// <see cref="HasSettlement"/> is <see langword="true"/> the run reached a
/// settlement (clear/result screen) and <see cref="PassTimeSeconds"/> /
/// <see cref="MasterModeScore"/> are populated.
/// </summary>
public readonly struct DungeonSyncResult
{
    /// <summary>The per-run unique scene id (<c>DungeonSyncData.scene_uuid</c>, field 1). Non-zero on a valid parse.</summary>
    public long SceneUuid { get; init; }

    /// <summary>True when a <c>DungeonSettlement</c> (field 7) was present — i.e. the run reached its clear/result screen.</summary>
    public bool HasSettlement { get; init; }

    /// <summary>Clear time in seconds (<c>DungeonSettlement.pass_time</c>, field 1). Only meaningful when <see cref="HasSettlement"/>.</summary>
    public int PassTimeSeconds { get; init; }

    /// <summary>Master-mode score (<c>DungeonSettlement.master_mode_score</c>, field 5). Only meaningful when <see cref="HasSettlement"/>.</summary>
    public int MasterModeScore { get; init; }
}

/// <summary>
/// Pure structural parser for <c>WorldNtf.SyncDungeonData { DungeonSyncData
/// v_data = 1 }</c>. Used by the Infrastructure dungeon probe to recognise a
/// dungeon-sync packet WITHOUT a known method id: every WorldNtf payload is fed
/// through <see cref="TryRead"/>, which accepts ONLY when the shape matches
/// (field 1 = <c>scene_uuid</c>, a non-zero varint int64) — so an arbitrary
/// non-dungeon WorldNtf method does not produce a false positive.
///
/// <para>
/// Wire layout (per offline recon — confirm method id in-game):
/// <code>
/// message SyncDungeonData   { DungeonSyncData   v_data       = 1; }   // WorldNtf method body
/// message DungeonSyncData   { int64             scene_uuid   = 1;     // -> level_uuid
///                             DungeonDamage     damage       = 5;
///                             DungeonSettlement settlement   = 7;
///                             DungeonScore      dungeon_score = 14; }
/// message DungeonSettlement { int32             pass_time        = 1;
///                             int32             master_mode_score = 5; }
/// </code>
/// </para>
///
/// <para>
/// BCL-only and side-effect-free (mirrors <c>WireProtocol</c> / the other Wire
/// readers) so it is unit-testable without IL2CPP / BepInEx. Defensive Try*
/// throughout — malformed input short-circuits to <see langword="false"/>,
/// never an exception.
/// </para>
/// </summary>
public static class DungeonSyncReader
{
    /// <summary>
    /// Attempt to decode <paramref name="worldNtfBody"/> as a
    /// <c>WorldNtf.SyncDungeonData</c> packet. The outer body is the single
    /// <c>v_data = 1</c> envelope; this method unwraps it then parses
    /// <c>DungeonSyncData</c>. Returns <see langword="true"/> only when
    /// <c>scene_uuid</c> (field 1) is present and non-zero.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> worldNtfBody, out DungeonSyncResult result)
    {
        result = default;

        // Outer envelope: SyncDungeonData { DungeonSyncData v_data = 1 }.
        // (field=1, wire=2) length-delimited.
        if (!WireProtocol.TryReadVRequest(worldNtfBody, out var data))
            return false;

        return TryReadDungeonSyncData(data, out result);
    }

    /// <summary>
    /// Parse a bare <c>DungeonSyncData</c> message body (the inner span, after
    /// the <c>v_data</c> unwrap). Exposed for direct unit testing of the inner
    /// shape. Accepts only when <c>scene_uuid</c> (field 1, varint) is non-zero.
    /// </summary>
    public static bool TryReadDungeonSyncData(ReadOnlySpan<byte> data, out DungeonSyncResult result)
    {
        result = default;

        long sceneUuid = 0;
        bool hasSettlement = false;
        int passTime = 0;
        int masterModeScore = 0;

        int pos = 0;
        while (pos < data.Length)
        {
            if (!WireProtocol.TryReadTag(data, ref pos, out var field, out var wire))
                return false;

            if (field == 1 && wire == 0)
            {
                // scene_uuid (int64 varint).
                if (!WireProtocol.TryReadVarint(data, ref pos, out var v)) return false;
                sceneUuid = (long)v;
            }
            else if (field == 7 && wire == 2)
            {
                // settlement (DungeonSettlement sub-message).
                if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var settlement)) return false;
                if (TryReadSettlement(settlement, out passTime, out masterModeScore))
                    hasSettlement = true;
            }
            else if (!WireProtocol.SkipField(data, ref pos, wire))
            {
                return false;
            }
        }

        // Reject anything that doesn't carry a non-zero per-run scene id — this is
        // the structural gate that keeps non-dungeon WorldNtf methods from matching.
        if (sceneUuid == 0) return false;

        result = new DungeonSyncResult
        {
            SceneUuid       = sceneUuid,
            HasSettlement   = hasSettlement,
            PassTimeSeconds = passTime,
            MasterModeScore = masterModeScore,
        };
        return true;
    }

    // DungeonSettlement { int32 pass_time = 1; int32 master_mode_score = 5; }
    private static bool TryReadSettlement(ReadOnlySpan<byte> span, out int passTime, out int masterModeScore)
    {
        passTime = 0;
        masterModeScore = 0;

        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
                return false;

            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(span, ref pos, out var v)) return false;
                passTime = (int)v;
            }
            else if (field == 5 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(span, ref pos, out var v)) return false;
                masterModeScore = (int)v;
            }
            else if (!WireProtocol.SkipField(span, ref pos, wire))
            {
                return false;
            }
        }
        return true;
    }
}
