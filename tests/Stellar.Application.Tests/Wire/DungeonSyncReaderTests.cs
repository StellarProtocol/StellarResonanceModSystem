using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="DungeonSyncReader"/> — the pure structural parser
/// for <c>WorldNtf.SyncDungeonData</c>. Payloads are hand-built protobuf bytes;
/// no IL2CPP / BepInEx / Unity dependencies.
/// </summary>
public sealed class DungeonSyncReaderTests
{
    [Fact]
    public void Reads_scene_uuid_and_settlement_from_full_payload()
    {
        // DungeonSettlement { pass_time(1)=372, master_mode_score(5)=8800 }
        var settlement = Msg(
            Varint(1, 372),
            Varint(5, 8800));

        // DungeonSyncData { scene_uuid(1)=123456789012345, damage(5)=<noise>, settlement(7)=... }
        var data = Msg(
            Varint(1, 123456789012345L),
            LenDelim(5, new byte[] { 0xAA, 0xBB }),   // damage sub-message (ignored)
            LenDelim(7, settlement));

        // SyncDungeonData { v_data(1) = DungeonSyncData }
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.Equal(123456789012345L, r.SceneUuid);
        Assert.True(r.HasSettlement);
        Assert.Equal(372, r.PassTimeSeconds);
        Assert.Equal(8800, r.MasterModeScore);
    }

    [Fact]
    public void Reads_total_score_from_dungeon_score_alongside_master_mode_score()
    {
        // The real Depths-of-Decay-M20 shape: settlement carries master_mode_score
        // (max/par 700) while the SEPARATE dungeon_score(14) carries the achieved
        // total_score (686). The recurring bug read only field 5 → showed 700.
        var settlement = Msg(
            Varint(1, 796),    // pass_time
            Varint(5, 700));   // master_mode_score (max/par)
        var dungeonScore = Msg(
            Varint(1, 686),    // total_score (achieved — the settlement screen's number)
            Varint(2, 98));    // cur_ratio (ignored)

        // DungeonSyncData { scene_uuid(1)=..., dungeon_score(14)=..., settlement(7)=... }
        var data = Msg(
            Varint(1, 588872378860175360L),
            LenDelim(14, dungeonScore),
            LenDelim(7, settlement));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.HasSettlement);
        Assert.Equal(700, r.MasterModeScore);
        Assert.True(r.HasScore);
        Assert.Equal(686, r.TotalScore);
    }

    [Fact]
    public void Reads_dungeon_scene_info_difficulty()
    {
        // DungeonSceneInfo { difficulty(1) = 6 }
        var sceneInfo = Msg(Varint(1, 6));

        // DungeonSyncData { scene_uuid(1)=99, dungeon_scene_info(21)=... }
        var data = Msg(
            Varint(1, 99L),
            LenDelim(21, sceneInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.Equal(99L, r.SceneUuid);
        Assert.True(r.HasDungeonSceneInfo);
        Assert.Equal(6, r.DungeonDifficulty);
    }

    [Fact]
    public void Reads_run_id_without_dungeon_scene_info()
    {
        var data = Msg(Varint(1, 99L));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.False(r.HasDungeonSceneInfo);
        Assert.Equal(0, r.DungeonDifficulty);
    }

    [Fact]
    public void Reads_dungeon_timer_info_start_time()
    {
        // DungeonTimerInfo { type(1)=1, start_time(2)=1700000000, dungeon_times(3)=600,
        //                    direction(4)=1, pause_time(8)=0, pause_total_time(9)=0,
        //                    cur_pause_timestamp(11)=0 }
        var timerInfo = Msg(
            Varint(1, 1),
            Varint(2, 1700000000),
            Varint(3, 600),
            Varint(4, 1));

        // DungeonSyncData { scene_uuid(1)=99, timer_info(15)=... }
        var data = Msg(
            Varint(1, 99L),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.Equal(99L, r.SceneUuid);
        Assert.True(r.HasTimerInfo);
        Assert.Equal(1700000000L * 1000L, r.RunTimerStartMs);
        Assert.Equal(1, r.TimerType);
        Assert.Equal(600, r.TimerDungeonTimes);
        Assert.Equal(1, r.TimerDirection);
    }

    [Fact]
    public void Reads_dungeon_flow_info_play_time()
    {
        // DungeonFlowInfo { state(1)=3, active_time(2)=1700000100, ready_time(3)=1700000110,
        //                   play_time(4)=1700000123, end_time(5)=0, settlement_time(6)=0,
        //                   dungeon_times(7)=600, result(8)=2 }
        var flowInfo = Msg(
            Varint(1, 3),
            Varint(2, 1700000100),
            Varint(3, 1700000110),
            Varint(4, 1700000123),
            Varint(7, 600),
            Varint(8, 2));

        // DungeonSyncData { scene_uuid(1)=99, flow_info(2)=... }
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.Equal(99L, r.SceneUuid);
        Assert.True(r.HasFlowInfo);
        Assert.Equal(3, r.FlowInfo.State);
        Assert.Equal(1700000100, r.FlowInfo.ActiveTime);
        Assert.Equal(1700000110, r.FlowInfo.ReadyTime);
        Assert.Equal(1700000123, r.FlowInfo.PlayTime);
        Assert.Equal(0, r.FlowInfo.EndTime);
        Assert.Equal(0, r.FlowInfo.SettlementTime);
        Assert.Equal(600, r.FlowInfo.DungeonTimes);
        Assert.Equal(2, r.FlowInfo.Result);
        // RunTimerStartMs source math: play_time (epoch s) * 1000.
        Assert.Equal(1700000123L * 1000L, r.FlowInfo.PlayTimeMs);
    }

    [Fact]
    public void Reads_zero_valued_flow_info_as_present_with_zero_play_time()
    {
        // The live-falsification shape: first delivery is a hub scene whose
        // flow fields are all zero. It must parse as PRESENT (HasFlowInfo) with
        // play_time 0 — the no-latch decision is the probe/service's, keyed on
        // PlayTime != 0.
        var flowInfo = Msg(
            Varint(1, 1),       // state only; all timers zero/absent
            Varint(4, 0));
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.HasFlowInfo);
        Assert.Equal(0, r.FlowInfo.PlayTime);
        Assert.Equal(0L, r.FlowInfo.PlayTimeMs);
    }

    [Fact]
    public void TryGetRunTimerStart_BothSourcesNonZero_TimerInfoWins()
    {
        // HUD priority: the game's own dungeon clock derives from
        // timer_info.start_time (dungeon_timer_vm.lua getEndTimeStamp), so when
        // one delivery carries BOTH non-zero sources the timer_info value wins.
        var timerInfo = Msg(Varint(2, 1700000000));          // start_time
        var flowInfo  = Msg(Varint(4, 1700000123));          // play_time
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(1700000000L * 1000L, startMs);
        Assert.Equal(DungeonRunTimerSource.TimerInfo, source);
    }

    [Fact]
    public void TryGetRunTimerStart_TimerZero_FallsBackToFlowPlayTime()
    {
        // Early hub deliveries carry an all-zero timer_info — a zero start_time
        // must never win; flow_info.play_time is the next candidate.
        var timerInfo = Msg(Varint(1, 1), Varint(2, 0));     // type only, start_time 0
        var flowInfo  = Msg(Varint(4, 1700000123));          // play_time
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(1700000123L * 1000L, startMs);
        Assert.Equal(DungeonRunTimerSource.FlowPlayTime, source);
    }

    [Fact]
    public void TryGetRunTimerStart_TimerInfoOnly_UsesTimerInfo()
    {
        var timerInfo = Msg(Varint(2, 1700000000));
        var data = Msg(
            Varint(1, 99L),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(1700000000L * 1000L, startMs);
        Assert.Equal(DungeonRunTimerSource.TimerInfo, source);
    }

    [Fact]
    public void TryGetRunTimerStart_OnlyActiveTimeNonZero_UsesActiveTimeLastResort()
    {
        // THE live entry-sync shape (instrumented Master run): state=1 Active,
        // active_time = live epoch, ready/play/end all zero, timer_info all
        // zero. active_time is the ONLY payload-borne clock the live path ever
        // delivers — it must be selected, flagged as the approximate source.
        var timerInfo = Msg(Varint(1, 1), Varint(2, 0));
        var flowInfo  = Msg(
            Varint(1, 1),               // state = 1 (Active)
            Varint(2, 1783186742),      // active_time = live epoch seconds
            Varint(3, 0),
            Varint(4, 0),
            Varint(5, 0));
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(1783186742L * 1000L, startMs);
        Assert.Equal(DungeonRunTimerSource.FlowActiveTime, source);
    }

    [Fact]
    public void TryGetRunTimerStart_PlayTimeBeatsActiveTime()
    {
        // Within one payload the exact play-start epoch outranks the
        // approximate entry-time epoch.
        var flowInfo = Msg(
            Varint(2, 1700000100),      // active_time (approx)
            Varint(4, 1700000123));     // play_time (exact)
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.True(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(1700000123L * 1000L, startMs);
        Assert.Equal(DungeonRunTimerSource.FlowPlayTime, source);
    }

    [Fact]
    public void TryGetRunTimerStart_AllSourcesZero_ReturnsFalse()
    {
        // Pre-start hub delivery: both sub-messages present, all clocks zero
        // (incl. active_time) — nothing must latch (stays 0 downstream).
        var timerInfo = Msg(Varint(1, 1), Varint(2, 0));
        var flowInfo  = Msg(Varint(1, 1), Varint(2, 0), Varint(4, 0));
        var data = Msg(
            Varint(1, 99L),
            LenDelim(2, flowInfo),
            LenDelim(15, timerInfo));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.False(r.TryGetRunTimerStart(out var startMs, out var source));
        Assert.Equal(0L, startMs);
        Assert.Equal(DungeonRunTimerSource.None, source);
    }

    [Fact]
    public void TryGetRunTimerStart_NeitherSourcePresent_ReturnsFalse()
    {
        var data = Msg(Varint(1, 99L));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.False(r.TryGetRunTimerStart(out var startMs, out _));
        Assert.Equal(0L, startMs);
    }

    [Fact]
    public void Reads_run_id_without_flow_info()
    {
        var data = Msg(Varint(1, 99L));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.False(r.HasFlowInfo);
        Assert.Equal(0, r.FlowInfo.PlayTime);
    }

    [Fact]
    public void Reads_run_id_without_dungeon_timer_info()
    {
        var data = Msg(Varint(1, 99L));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.False(r.HasTimerInfo);
        Assert.Equal(0L, r.RunTimerStartMs);
    }

    [Fact]
    public void Reads_run_id_without_settlement()
    {
        var data = Msg(Varint(1, 42L));
        var body = LenDelim(1, data);

        Assert.True(DungeonSyncReader.TryRead(body, out var r));
        Assert.Equal(42L, r.SceneUuid);
        Assert.False(r.HasSettlement);
        Assert.Equal(0, r.PassTimeSeconds);
        Assert.Equal(0, r.MasterModeScore);
    }

    [Fact]
    public void Rejects_payload_with_zero_scene_uuid()
    {
        var data = Msg(Varint(1, 0L));
        var body = LenDelim(1, data);
        Assert.False(DungeonSyncReader.TryRead(body, out _));
    }

    [Fact]
    public void Rejects_payload_with_no_scene_uuid_field()
    {
        // Only a settlement-shaped field 7, no scene_uuid → structural reject.
        var data = Msg(LenDelim(7, Msg(Varint(1, 10))));
        var body = LenDelim(1, data);
        Assert.False(DungeonSyncReader.TryRead(body, out _));
    }

    [Fact]
    public void Rejects_non_dungeon_worldntf_body()
    {
        // A different WorldNtf method whose field 1 is a length-delimited
        // sub-message (not a varint scene_uuid). Unwrap succeeds but the inner
        // message has no field-1 varint → reject.
        var inner = Msg(LenDelim(1, new byte[] { 1, 2, 3 }));
        var body = LenDelim(1, inner);
        Assert.False(DungeonSyncReader.TryRead(body, out _));
    }

    [Fact]
    public void Rejects_empty_payload()
    {
        Assert.False(DungeonSyncReader.TryRead(System.Array.Empty<byte>(), out _));
    }

    // ── minimal protobuf writers ────────────────────────────────────────────

    private static byte[] Varint(int field, long value)
    {
        var b = new List<byte>();
        WriteTag(b, field, 0);
        WriteVarint(b, (ulong)value);
        return b.ToArray();
    }

    private static byte[] LenDelim(int field, byte[] payload)
    {
        var b = new List<byte>();
        WriteTag(b, field, 2);
        WriteVarint(b, (ulong)payload.Length);
        b.AddRange(payload);
        return b.ToArray();
    }

    private static byte[] Msg(params byte[][] fields)
    {
        var b = new List<byte>();
        foreach (var f in fields) b.AddRange(f);
        return b.ToArray();
    }

    private static void WriteTag(List<byte> b, int field, int wire)
        => WriteVarint(b, (ulong)((field << 3) | wire));

    private static void WriteVarint(List<byte> b, ulong v)
    {
        do
        {
            byte cur = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) cur |= 0x80;
            b.Add(cur);
        } while (v != 0);
    }
}
