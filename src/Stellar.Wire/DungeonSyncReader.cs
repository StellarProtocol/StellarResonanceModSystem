using System;

namespace Stellar.Wire;

/// <summary>
/// Pure structural parser for <c>WorldNtf.SyncDungeonData { DungeonSyncData
/// v_data = 1 }</c>. Used by the Infrastructure dungeon probe to recognise a
/// dungeon-sync packet WITHOUT a known method id: every WorldNtf payload is fed
/// through <see cref="TryRead"/>, which accepts ONLY when the shape matches
/// (field 1 = <c>scene_uuid</c>, a non-zero varint int64) — so an arbitrary
/// non-dungeon WorldNtf method does not produce a false positive.
///
/// <para>
/// Wire layout (per offline recon; WorldNtf method id 23 confirmed in-game —
/// see <c>WorldNtfMethodIds.SyncDungeonData</c>):
/// <code>
/// message SyncDungeonData   { DungeonSyncData   v_data       = 1; }   // WorldNtf method body
/// message DungeonSyncData   { int64             scene_uuid   = 1;     // -> level_uuid
///                             DungeonFlowInfo   flow_info    = 2;     // FALLBACK run-timer source (play_time)
///                             DungeonDamage     damage       = 5;
///                             DungeonSettlement settlement   = 7;
///                             DungeonScore      dungeon_score = 14;
///                             DungeonTimerInfo  timer_info    = 15;
///                             DungeonSceneInfo  dungeon_scene_info = 21; }
/// message DungeonSettlement { int32             pass_time        = 1;
///                             int32             master_mode_score = 5; }
/// message DungeonSceneInfo  { int32             difficulty       = 1; } // semantic unconfirmed (see DungeonDifficulty)
/// message DungeonFlowInfo   { EDungeonState     state            = 1;   // varint enum
///                             int32             active_time      = 2;
///                             int32             ready_time       = 3;
///                             int32             play_time        = 4;   // epoch (assumed s) when play begins — FALLBACK RunTimerStartMs source
///                             int32             end_time         = 5;
///                             int32             settlement_time  = 6;
///                             int32             dungeon_times    = 7;
///                             int32             result           = 8; } // future kill/partial verdict candidate (diag only)
/// message DungeonTimerInfo  { int32             type             = 1;
///                             int32             start_time       = 2;   // epoch s — PRIMARY run-timer source (HUD-authoritative; dungeon_timer_vm.lua)
///                             int32             dungeon_times    = 3;
///                             int32             direction        = 4;
///                             int32             index            = 5;
///                             int32             change_time      = 6;
///                             int32             effect_type      = 7;
///                             int32             pause_time       = 8;
///                             int32             pause_total_time = 9;
///                             int32             out_look_type    = 10;
///                             int32             cur_pause_timestamp = 11; }
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

        var acc = new FieldAccumulator();

        int pos = 0;
        while (pos < data.Length)
        {
            if (!WireProtocol.TryReadTag(data, ref pos, out var field, out var wire))
                return false;

            if (!TryApplyField(data, ref pos, field, wire, ref acc))
                return false;
        }

        // Reject anything that doesn't carry a non-zero per-run scene id — this is
        // the structural gate that keeps non-dungeon WorldNtf methods from matching.
        if (acc.SceneUuid == 0) return false;

        result = new DungeonSyncResult
        {
            SceneUuid           = acc.SceneUuid,
            HasSettlement       = acc.HasSettlement,
            PassTimeSeconds     = acc.PassTime,
            MasterModeScore     = acc.MasterModeScore,
            HasDungeonSceneInfo = acc.HasDungeonSceneInfo,
            DungeonDifficulty   = acc.DungeonDifficulty,
            HasFlowInfo         = acc.HasFlowInfo,
            FlowInfo            = acc.FlowInfo,
            HasTimerInfo        = acc.HasTimerInfo,
            RunTimerStartMs     = acc.RunTimerStartMs,
            TimerType               = acc.TimerType,
            TimerDungeonTimes       = acc.TimerDungeonTimes,
            TimerDirection          = acc.TimerDirection,
            TimerPauseTime          = acc.TimerPauseTime,
            TimerPauseTotalTime     = acc.TimerPauseTotalTime,
            TimerCurPauseTimestamp  = acc.TimerCurPauseTimestamp,
        };
        return true;
    }

    // Mutable scratch accumulator for the single-pass field walk in
    // TryReadDungeonSyncData — keeps that method's body short enough to satisfy
    // the STELLAR0002 (>50 LoC) gate while still doing one linear pass.
    private struct FieldAccumulator
    {
        public long SceneUuid;
        public bool HasSettlement;
        public int PassTime;
        public int MasterModeScore;
        public bool HasDungeonSceneInfo;
        public int DungeonDifficulty;
        public bool HasFlowInfo;
        public DungeonFlowInfo FlowInfo;
        public bool HasTimerInfo;
        public long RunTimerStartMs;
        public int TimerType;
        public int TimerDungeonTimes;
        public int TimerDirection;
        public int TimerPauseTime;
        public int TimerPauseTotalTime;
        public int TimerCurPauseTimestamp;
    }

    // Dispatch a single decoded (field, wire) tag into the accumulator. Returns
    // false only on a structurally malformed field (short read / bad varint).
    private static bool TryApplyField(ReadOnlySpan<byte> data, ref int pos, int field, int wire, ref FieldAccumulator acc)
    {
        if (field == 1 && wire == 0)
        {
            // scene_uuid (int64 varint).
            if (!WireProtocol.TryReadVarint(data, ref pos, out var v)) return false;
            acc.SceneUuid = (long)v;
            return true;
        }

        if (field == 7 && wire == 2)
        {
            // settlement (DungeonSettlement sub-message).
            if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var settlement)) return false;
            if (TryReadSettlement(settlement, out var passTime, out var masterModeScore))
            {
                acc.HasSettlement = true;
                acc.PassTime = passTime;
                acc.MasterModeScore = masterModeScore;
            }
            return true;
        }

        if (field == 21 && wire == 2)
        {
            // dungeon_scene_info (DungeonSceneInfo sub-message) — carries the
            // dungeon challenge-level/tier value; semantic unconfirmed, see
            // DungeonSyncResult.DungeonDifficulty.
            if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var sceneInfo)) return false;
            if (TryReadDungeonSceneInfo(sceneInfo, out var difficulty))
            {
                acc.HasDungeonSceneInfo = true;
                acc.DungeonDifficulty = difficulty;
            }
            return true;
        }

        if (field == 2 && wire == 2)
            return TryApplyFlowInfoField(data, ref pos, ref acc);

        if (field == 15 && wire == 2)
            return TryApplyTimerInfoField(data, ref pos, ref acc);

        return WireProtocol.SkipField(data, ref pos, wire);
    }

    // flow_info (DungeonFlowInfo sub-message, field 2) — the dungeon flow
    // state-machine snapshot; play_time is the FALLBACK run-timer start source
    // (primary: timer_info.start_time). Split out of TryApplyField to
    // keep that method's body under the STELLAR0002 (>50 LoC) gate.
    private static bool TryApplyFlowInfoField(ReadOnlySpan<byte> data, ref int pos, ref FieldAccumulator acc)
    {
        if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var flowInfo)) return false;
        if (TryReadDungeonFlowInfo(flowInfo, out var flow))
        {
            acc.HasFlowInfo = true;
            acc.FlowInfo = flow;
        }
        return true;
    }

    // timer_info (DungeonTimerInfo sub-message, field 15) — carries the PRIMARY
    // (HUD-authoritative) run-timer start time; see
    // DungeonSyncResult.RunTimerStartMs. Split out of TryApplyField to keep
    // that method's body under the STELLAR0002 (>50 LoC) gate.
    private static bool TryApplyTimerInfoField(ReadOnlySpan<byte> data, ref int pos, ref FieldAccumulator acc)
    {
        if (!WireProtocol.TryReadLengthDelimited(data, ref pos, out var timerInfo)) return false;
        if (TryReadDungeonTimerInfo(timerInfo, out var raw))
        {
            acc.HasTimerInfo = true;
            acc.RunTimerStartMs = raw.StartTime * 1000L;
            acc.TimerType = raw.Type;
            acc.TimerDungeonTimes = raw.DungeonTimes;
            acc.TimerDirection = raw.Direction;
            acc.TimerPauseTime = raw.PauseTime;
            acc.TimerPauseTotalTime = raw.PauseTotalTime;
            acc.TimerCurPauseTimestamp = raw.CurPauseTimestamp;
        }
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

    // DungeonSceneInfo { int32 difficulty = 1; } — semantic unconfirmed, see
    // DungeonSyncResult.DungeonDifficulty.
    private static bool TryReadDungeonSceneInfo(ReadOnlySpan<byte> span, out int difficulty)
    {
        difficulty = 0;

        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
                return false;

            if (field == 1 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(span, ref pos, out var v)) return false;
                difficulty = (int)v;
            }
            else if (!WireProtocol.SkipField(span, ref pos, wire))
            {
                return false;
            }
        }
        return true;
    }

    // Raw fields lifted out of DungeonTimerInfo — StartTime is the field of
    // interest (PRIMARY run-timer source, see DungeonSyncResult.RunTimerStartMs);
    // the rest are carried purely for the diagnostic log. Grouping them in a struct keeps
    // TryReadDungeonTimerInfo's signature under the STELLAR0003 (>5 params) gate.
    private struct DungeonTimerRaw
    {
        public int Type;
        public int StartTime;
        public int DungeonTimes;
        public int Direction;
        public int PauseTime;
        public int PauseTotalTime;
        public int CurPauseTimestamp;
    }

    // DungeonTimerInfo { type(1), start_time(2), dungeon_times(3), direction(4),
    // index(5), change_time(6), effect_type(7), pause_time(8),
    // pause_total_time(9), out_look_type(10), cur_pause_timestamp(11) } — only
    // the fields consumed by RunTimerStartMs / the diagnostic log are decoded;
    // index/change_time/effect_type/out_look_type are skipped like any other
    // unknown field. start_time is the PRIMARY (HUD-authoritative) run-timer
    // source, see DungeonSyncResult.RunTimerStartMs.
    private static bool TryReadDungeonTimerInfo(ReadOnlySpan<byte> span, out DungeonTimerRaw raw)
    {
        raw = default;

        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
                return false;

            if (wire != 0)
            {
                if (!WireProtocol.SkipField(span, ref pos, wire)) return false;
                continue;
            }

            if (!WireProtocol.TryReadVarint(span, ref pos, out var v)) return false;
            ApplyTimerField(field, (int)v, ref raw);
        }
        return true;
    }

    // Dispatch a single decoded varint field into the DungeonTimerInfo scratch
    // struct. Split out of TryReadDungeonTimerInfo to keep that method's body
    // short (STELLAR0002).
    private static void ApplyTimerField(int field, int value, ref DungeonTimerRaw raw)
    {
        switch (field)
        {
            case 1: raw.Type = value; break;
            case 2: raw.StartTime = value; break;
            case 3: raw.DungeonTimes = value; break;
            case 4: raw.Direction = value; break;
            case 8: raw.PauseTime = value; break;
            case 9: raw.PauseTotalTime = value; break;
            case 11: raw.CurPauseTimestamp = value; break;
        }
    }

    // Mutable scratch for the DungeonFlowInfo walk — mirrors DungeonTimerRaw;
    // keeps ApplyFlowField's signature under the STELLAR0003 (>5 params) gate.
    private struct DungeonFlowRaw
    {
        public int State;
        public int ActiveTime;
        public int ReadyTime;
        public int PlayTime;
        public int EndTime;
        public int SettlementTime;
        public int DungeonTimes;
        public int Result;
    }

    // DungeonFlowInfo { state(1), active_time(2), ready_time(3), play_time(4),
    // end_time(5), settlement_time(6), dungeon_times(7), result(8) } — all
    // varints. play_time is the FALLBACK run-timer start (epoch, assumed seconds).
    private static bool TryReadDungeonFlowInfo(ReadOnlySpan<byte> span, out DungeonFlowInfo flow)
    {
        flow = default;
        var raw = new DungeonFlowRaw();

        int pos = 0;
        while (pos < span.Length)
        {
            if (!WireProtocol.TryReadTag(span, ref pos, out var field, out var wire))
                return false;

            if (wire != 0)
            {
                if (!WireProtocol.SkipField(span, ref pos, wire)) return false;
                continue;
            }

            if (!WireProtocol.TryReadVarint(span, ref pos, out var v)) return false;
            ApplyFlowField(field, (int)v, ref raw);
        }

        flow = new DungeonFlowInfo
        {
            State          = raw.State,
            ActiveTime     = raw.ActiveTime,
            ReadyTime      = raw.ReadyTime,
            PlayTime       = raw.PlayTime,
            EndTime        = raw.EndTime,
            SettlementTime = raw.SettlementTime,
            DungeonTimes   = raw.DungeonTimes,
            Result         = raw.Result,
        };
        return true;
    }

    // Dispatch a single decoded varint field into the DungeonFlowInfo scratch
    // struct. Split out of TryReadDungeonFlowInfo to keep that method's body
    // short (STELLAR0002).
    private static void ApplyFlowField(int field, int value, ref DungeonFlowRaw raw)
    {
        switch (field)
        {
            case 1: raw.State = value; break;
            case 2: raw.ActiveTime = value; break;
            case 3: raw.ReadyTime = value; break;
            case 4: raw.PlayTime = value; break;
            case 5: raw.EndTime = value; break;
            case 6: raw.SettlementTime = value; break;
            case 7: raw.DungeonTimes = value; break;
            case 8: raw.Result = value; break;
        }
    }
}
