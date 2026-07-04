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

    /// <summary>
    /// True when a <c>DungeonSceneInfo</c> (field 21) was present on this
    /// payload — i.e. <see cref="DungeonDifficulty"/> is meaningful.
    /// </summary>
    public bool HasDungeonSceneInfo { get; init; }

    /// <summary>
    /// Raw value of <c>DungeonSceneInfo.difficulty</c> (field 1, varint) inside
    /// <c>DungeonSyncData.dungeon_scene_info</c> (field 21). Only meaningful when
    /// <see cref="HasDungeonSceneInfo"/>.
    /// <para>
    /// <b>Semantic UNCONFIRMED</b>: this is the value the lobby's Master 1-20
    /// selector should land on, but whether it carries the raw 1-20 challenge
    /// level or a small tier enum (normal/hard/master) has not been verified
    /// against a real Master-tier run yet. Treat as a diagnostic value until
    /// confirmed; consumers should not assume it is the literal level number.
    /// </para>
    /// </summary>
    public int DungeonDifficulty { get; init; }

    /// <summary>
    /// Raw <c>DungeonTimerInfo</c> fields (<c>type</c>, <c>dungeon_times</c>,
    /// <c>direction</c>, <c>pause_time</c>, <c>pause_total_time</c>,
    /// <c>cur_pause_timestamp</c>) captured alongside <see cref="RunTimerStartMs"/>
    /// purely for the one-shot diagnostic log — not otherwise surfaced through
    /// <c>IDungeonState</c>. Only meaningful when <see cref="HasTimerInfo"/>.
    /// </summary>
    public int TimerType { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.dungeon_times</c> (field 3). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerDungeonTimes { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.direction</c> (field 4). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerDirection { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.pause_time</c> (field 8). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerPauseTime { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.pause_total_time</c> (field 9). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerPauseTotalTime { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.cur_pause_timestamp</c> (field 11). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerCurPauseTimestamp { get; init; }

    /// <summary>
    /// True when a <c>DungeonTimerInfo</c> (field 15) was present on this
    /// payload — i.e. <see cref="RunTimerStartMs"/> is meaningful.
    /// </summary>
    public bool HasTimerInfo { get; init; }

    /// <summary>
    /// Server epoch ms when the dungeon run-timer started, derived from
    /// <c>DungeonTimerInfo.start_time</c> (field 2, varint) inside
    /// <c>DungeonSyncData.timer_info</c> (field 15). Only meaningful when
    /// <see cref="HasTimerInfo"/>.
    /// <para>
    /// <b>Semantic UNCONFIRMED</b>: <c>start_time</c> is assumed to be an epoch
    /// timestamp in SECONDS and is converted to ms here via <c>* 1000L</c>, but
    /// this has not been verified against a real dungeon run's wall-clock time.
    /// Treat as a diagnostic value until confirmed — same caveat as
    /// <see cref="DungeonDifficulty"/> carried before its first live run.
    /// </para>
    /// </summary>
    public long RunTimerStartMs { get; init; }
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
/// Wire layout (per offline recon; WorldNtf method id 23 confirmed in-game —
/// see <c>WorldNtfMethodIds.SyncDungeonData</c>):
/// <code>
/// message SyncDungeonData   { DungeonSyncData   v_data       = 1; }   // WorldNtf method body
/// message DungeonSyncData   { int64             scene_uuid   = 1;     // -> level_uuid
///                             DungeonDamage     damage       = 5;
///                             DungeonSettlement settlement   = 7;
///                             DungeonScore      dungeon_score = 14;
///                             DungeonTimerInfo  timer_info    = 15;
///                             DungeonSceneInfo  dungeon_scene_info = 21; }
/// message DungeonSettlement { int32             pass_time        = 1;
///                             int32             master_mode_score = 5; }
/// message DungeonSceneInfo  { int32             difficulty       = 1; } // semantic unconfirmed (see DungeonDifficulty)
/// message DungeonTimerInfo  { int32             type             = 1;
///                             int32             start_time       = 2;   // semantic unconfirmed (see RunTimerStartMs)
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

        if (field == 15 && wire == 2)
            return TryApplyTimerInfoField(data, ref pos, ref acc);

        return WireProtocol.SkipField(data, ref pos, wire);
    }

    // timer_info (DungeonTimerInfo sub-message, field 15) — carries the
    // run-timer start time; semantic unconfirmed, see
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
    // interest (see DungeonSyncResult.RunTimerStartMs); the rest are carried
    // purely for the one-shot diagnostic log. Grouping them in a struct keeps
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
    // unknown field. Semantic of start_time is unconfirmed, see
    // DungeonSyncResult.RunTimerStartMs.
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
}
