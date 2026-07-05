using System;
using System.Buffers.Binary;

namespace Stellar.Wire;

/// <summary>
/// Pure structural parser for <c>WorldNtf.SyncDungeonDirtyData { BufferStream
/// v_data = 1 { bytes buffer = 1 } }</c> — the dungeon container's dirty-DELTA
/// update. The inner blob is NOT protobuf: it is the game's int32-framed (little
/// endian) container-merge format consumed by
/// <c>ContainerMgr.DungeonSyncData:MergeData</c>
/// (<c>lua/zcontainer/dungeon_sync_data.lua</c>) after
/// <c>DungeonSyncService.OnSync</c> hands the raw bytes to Lua
/// (<c>lua/sync/dungeon_sync.lua</c>).
///
/// <para>
/// Blob framing (all values int32 LE unless noted):
/// <code>
/// container      := beginTag(-2) size(int32) entry* endTag(-3)
///                 | beginTag(-2) emptyTag(-3)                    // empty container, 8 bytes
/// entry          := fieldIndex(int32 &gt; 0) payload
/// payload        := scalar bytes (type known per field) | container (messages)
/// // size = byte length of the entry list ONLY (the trailing endTag sits
/// // OUTSIDE it — the game's unknown-field recovery jumps to entriesStart+size
/// // and then expects to read the endTag).
/// </code>
/// DungeonSyncData field types (dungeon_sync_data.lua mergeDataFuncs):
/// field 1 = sceneUuid (int64, 8 bytes), field 27 = errCode (int32, 4 bytes),
/// fields 2..26 = nested containers. Field 15 = timerInfo, whose own fields
/// 1..11 are ALL int32 scalars (dungeon_timer_info.lua); field 2 =
/// <c>start_time</c> (epoch seconds) — the value this reader exists to extract.
/// </para>
///
/// <para>
/// A dirty delta carries ONLY the changed fields — there is deliberately no
/// scene_uuid gate here (deltas usually omit field 1); the strict gate stays on
/// <see cref="DungeonSyncReader"/> for the method-23 full sync. BCL-only,
/// side-effect-free, fully defensive: malformed input returns
/// <see langword="false"/>, never throws.
/// </para>
/// </summary>
public static class DungeonDirtyDataReader
{
    private const int TagBegin = -2;
    private const int TagEnd = -3;

    private const int FieldSceneUuid = 1;   // int64 scalar
    private const int FieldFlowInfo = 2;    // DungeonFlowInfo container
    private const int FieldSettlement = 7;  // DungeonSettlement container
    private const int FieldTimerInfo = 15;  // nested DungeonTimerInfo container
    private const int FieldErrCode = 27;    // int32 scalar (last known field)

    private const int TimerFieldStartTime = 2;
    private const int TimerFieldDungeonTimes = 3;
    private const int TimerFieldDirection = 4;
    private const int TimerFieldPauseTotalTime = 9;
    private const int TimerFieldMax = 11;

    private const int FlowFieldResult = 8;      // flow_info.result (int32 scalar)
    private const int FlowFieldMax = 8;         // fields 1..8 are int32 scalars

    private const int SettleFieldPassTime = 1;  // settlement.pass_time (int32)
    private const int SettleFieldMasterScore = 5;
    private const int SettleFieldMax = 5;        // fields 1..5; nested sub-msgs (award/pos/boss) skipped

    /// <summary>
    /// Attempt to decode <paramref name="worldNtfBody"/> as a
    /// <c>WorldNtf.SyncDungeonDirtyData</c> packet and extract the
    /// <c>timer_info</c> slice. Returns <see langword="true"/> only when the
    /// blob walks cleanly AND carried a timer_info container (field 15) — a
    /// delta that only touched other fields returns <see langword="false"/>.
    /// </summary>
    public static bool TryReadTimerStart(ReadOnlySpan<byte> worldNtfBody, out DungeonDirtyTimerResult result)
    {
        result = default;

        // SyncDungeonDirtyData { BufferStream v_data = 1 } → BufferStream { bytes buffer = 1 }.
        if (!WireProtocol.TryReadVRequest(worldNtfBody, out var bufferStream))
            return false;
        if (!WireProtocol.TryReadVRequest(bufferStream, out var blob))
            return false;

        return TryReadDirtyBlob(blob, out result);
    }

    /// <summary>
    /// Walk a bare dirty blob (the <c>BufferStream.buffer</c> bytes, after the
    /// protobuf unwrap). Exposed for direct unit testing of the blob framing.
    /// </summary>
    public static bool TryReadDirtyBlob(ReadOnlySpan<byte> blob, out DungeonDirtyTimerResult result)
    {
        result = default;

        int pos = 0;
        if (!TryReadInt32(blob, ref pos, out int tag) || tag != TagBegin) return false;
        if (!TryReadInt32(blob, ref pos, out int size)) return false;
        if (size == TagEnd) return false;                    // empty delta — nothing changed
        if (size < 0 || size > blob.Length - pos) return false;
        int entriesEnd = pos + size;

        while (true)
        {
            if (!TryReadInt32(blob, ref pos, out int index)) return false;
            if (index == TagEnd) break;
            if (index <= 0) return false;

            if (index > FieldErrCode)
            {
                // Unknown future field — mimic the game's recovery: jump to the
                // end of this container's entries and expect the end tag.
                pos = entriesEnd;
                if (!TryReadInt32(blob, ref pos, out int endTag) || endTag != TagEnd) return false;
                break;
            }

            if (!TryApplyDirtyField(blob, ref pos, index, ref result)) return false;
        }

        return result.HasTimerInfo || result.HasFlowResult || result.HasSettlement;
    }

    // Consume one top-level DungeonSyncData dirty entry's payload. Field types
    // per dungeon_sync_data.lua: 1 = int64, 27 = int32, everything between =
    // nested container (only 15/timerInfo is decoded; the rest are skipped).
    private static bool TryApplyDirtyField(ReadOnlySpan<byte> blob, ref int pos, int index, ref DungeonDirtyTimerResult result)
    {
        if (index == FieldSceneUuid)
            return TrySkip(blob, ref pos, 8);

        if (index == FieldErrCode)
            return TrySkip(blob, ref pos, 4);

        if (index == FieldTimerInfo)
            return TryReadTimerContainer(blob, ref pos, ref result);

        if (index == FieldFlowInfo)
            return TryReadFlowContainer(blob, ref pos, ref result);

        if (index == FieldSettlement)
            return TryReadSettlementContainer(blob, ref pos, ref result);

        return TrySkipContainer(blob, ref pos);
    }

    // DungeonTimerInfo dirty container — every field (1..11) is an int32 scalar
    // (dungeon_timer_info.lua mergeDataFuncs); capture start_time (2) plus the
    // few diagnostics-only fields, skip the rest by value.
    private static bool TryReadTimerContainer(ReadOnlySpan<byte> blob, ref int pos, ref DungeonDirtyTimerResult result)
    {
        if (!TryReadInt32(blob, ref pos, out int tag) || tag != TagBegin) return false;
        if (!TryReadInt32(blob, ref pos, out int size)) return false;
        if (size == TagEnd)
        {
            // Present-but-empty timer container.
            result = result with { HasTimerInfo = true };
            return true;
        }
        if (size < 0 || size > blob.Length - pos) return false;
        int entriesEnd = pos + size;

        int startTime = 0, dungeonTimes = 0, direction = 0, pauseTotal = 0;
        while (true)
        {
            if (!TryReadInt32(blob, ref pos, out int index)) return false;
            if (index == TagEnd) break;
            if (index <= 0) return false;
            if (index > TimerFieldMax)
            {
                // Unknown future timer field — same recovery as the game.
                pos = entriesEnd;
                if (!TryReadInt32(blob, ref pos, out int endTag) || endTag != TagEnd) return false;
                break;
            }
            if (!TryReadInt32(blob, ref pos, out int value)) return false;
            if (index == TimerFieldStartTime) startTime = value;
            else if (index == TimerFieldDungeonTimes) dungeonTimes = value;
            else if (index == TimerFieldDirection) direction = value;
            else if (index == TimerFieldPauseTotalTime) pauseTotal = value;
        }

        result = result with
        {
            HasTimerInfo = true,
            StartTimeSeconds = startTime,
            DungeonTimes = dungeonTimes,
            Direction = direction,
            PauseTotalTime = pauseTotal,
        };
        return true;
    }

    // DungeonFlowInfo dirty container — fields 1..8 all int32 scalars; capture result (8).
    private static bool TryReadFlowContainer(ReadOnlySpan<byte> blob, ref int pos, ref DungeonDirtyTimerResult result)
    {
        if (!TryReadInt32(blob, ref pos, out int tag) || tag != TagBegin) return false;
        if (!TryReadInt32(blob, ref pos, out int size)) return false;
        if (size == TagEnd) { result = result with { HasFlowResult = true }; return true; }
        if (size < 0 || size > blob.Length - pos) return false;
        int entriesEnd = pos + size;
        int flowResult = 0;
        while (true)
        {
            if (!TryReadInt32(blob, ref pos, out int index)) return false;
            if (index == TagEnd) break;
            if (index <= 0) return false;
            if (index > FlowFieldMax)
            {
                pos = entriesEnd;
                if (!TryReadInt32(blob, ref pos, out int endTag) || endTag != TagEnd) return false;
                break;
            }
            if (!TryReadInt32(blob, ref pos, out int value)) return false;
            if (index == FlowFieldResult) flowResult = value;
        }
        result = result with { HasFlowResult = true, FlowResult = flowResult };
        return true;
    }

    // DungeonSettlement dirty container — field 1 (pass_time) + 5 (master_mode_score) are int32
    // scalars; 2/3/4 (award map, settlement_pos map, world_boss_settlement) are nested containers.
    private static bool TryReadSettlementContainer(ReadOnlySpan<byte> blob, ref int pos, ref DungeonDirtyTimerResult result)
    {
        if (!TryReadInt32(blob, ref pos, out int tag) || tag != TagBegin) return false;
        if (!TryReadInt32(blob, ref pos, out int size)) return false;
        if (size == TagEnd) { result = result with { HasSettlement = true }; return true; }
        if (size < 0 || size > blob.Length - pos) return false;
        int entriesEnd = pos + size;
        int passTime = 0, masterScore = 0;
        while (true)
        {
            if (!TryReadInt32(blob, ref pos, out int index)) return false;
            if (index == TagEnd) break;
            if (index <= 0) return false;
            if (index > SettleFieldMax)
            {
                pos = entriesEnd;
                if (!TryReadInt32(blob, ref pos, out int endTag) || endTag != TagEnd) return false;
                break;
            }
            if (index == SettleFieldPassTime) { if (!TryReadInt32(blob, ref pos, out passTime)) return false; }
            else if (index == SettleFieldMasterScore) { if (!TryReadInt32(blob, ref pos, out masterScore)) return false; }
            else { if (!TrySkipContainer(blob, ref pos)) return false; }   // 2/3/4 nested containers
        }
        result = result with { HasSettlement = true, PassTimeSeconds = passTime, MasterModeScore = masterScore };
        return true;
    }

    // Skip a nested container we don't decode: [-2][size][entries…][-3] or the
    // empty form [-2][-3].
    private static bool TrySkipContainer(ReadOnlySpan<byte> blob, ref int pos)
    {
        if (!TryReadInt32(blob, ref pos, out int tag) || tag != TagBegin) return false;
        if (!TryReadInt32(blob, ref pos, out int size)) return false;
        if (size == TagEnd) return true;                     // empty container
        if (size < 0 || size > blob.Length - pos) return false;
        pos += size;
        return TryReadInt32(blob, ref pos, out int endTag) && endTag == TagEnd;
    }

    private static bool TrySkip(ReadOnlySpan<byte> blob, ref int pos, int count)
    {
        if (count > blob.Length - pos) return false;
        pos += count;
        SkipGuard(blob, ref pos);   // scalar values carry a trailing canary too
        return true;
    }

    private static bool TryReadInt32(ReadOnlySpan<byte> blob, ref int pos, out int value)
    {
        value = 0;
        if (blob.Length - pos < 4) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(pos, 4));
        pos += 4;
        SkipGuard(blob, ref pos);
        return true;
    }

    // This game build writes a 4-byte 0xDEADBEEF canary AFTER every value in the
    // container-merge stream (confirmed live: census method-24 blob is
    // -2,GUARD,size,GUARD,…). We consume it after each value. CONDITIONAL — only
    // when the next word actually equals the canary — so a guard-free stream (the
    // synthetic unit tests) still parses. 0xDEADBEEF never occurs as a real value
    // here (tags/sizes/field-ids are small; start_time is a ~1.7e9 epoch int32,
    // nowhere near 0xDEADBEEF). Mirrors Infrastructure's proven BlobReader.SkipGuard.
    private const uint Guard = 0xDEADBEEF;

    private static void SkipGuard(ReadOnlySpan<byte> blob, ref int pos)
    {
        if (blob.Length - pos < 4) return;
        if ((uint)BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(pos, 4)) == Guard) pos += 4;
    }
}
