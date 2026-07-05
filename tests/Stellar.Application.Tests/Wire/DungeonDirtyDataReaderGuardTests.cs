using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

// Validates DungeonDirtyDataReader against the REAL wire framing: this game build
// writes a 4-byte 0xDEADBEEF canary after every int32/int64 value in the
// container-merge stream (confirmed live via the census: method-24 blob began
// -2,GUARD,size,GUARD,…). The recovered reader originally lacked the guard-skip
// and rejected every real delta (read the guard as a negative size). These tests
// lock in the guard-aware behaviour.
public class DungeonDirtyDataReaderGuardTests
{
    private const uint Guard = 0xDEADBEEF;

    // Append an int32 (LE) followed by the 0xDEADBEEF canary — one "guarded value".
    private static void V(List<byte> b, int v)
    {
        b.Add((byte)(v & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 24) & 0xFF));
        b.Add((byte)(Guard & 0xFF));
        b.Add((byte)((Guard >> 8) & 0xFF));
        b.Add((byte)((Guard >> 16) & 0xFF));
        b.Add((byte)((Guard >> 24) & 0xFF));
    }

    private const int TagBegin = -2;
    private const int TagEnd = -3;

    [Fact]
    public void GuardedTimerOnlyDelta_ExtractsStartTime()
    {
        // Top container: -2, size, [field 15 → timer container], -3
        // Timer container: -2, size, [field 2 = start_time], -3
        // size values only gate a bounds check + the unknown-field jump; the
        // happy path (field 15 then field 2) never navigates by them, so any
        // in-range positive value is fine.
        const int startTime = 1783196910;   // plausible 2026 epoch seconds
        var b = new List<byte>();
        V(b, TagBegin);   // top BEGIN
        V(b, 40);         // top size (in range, != -3)
        V(b, 15);         // field 15 = timerInfo
        V(b, TagBegin);   //   timer BEGIN
        V(b, 16);         //   timer size
        V(b, 2);          //   field 2 = start_time
        V(b, startTime);  //   value
        V(b, TagEnd);     //   timer END
        V(b, TagEnd);     // top END

        var ok = DungeonDirtyDataReader.TryReadDirtyBlob(b.ToArray(), out var result);

        Assert.True(ok);
        Assert.True(result.HasTimerInfo);
        Assert.Equal(startTime, result.StartTimeSeconds);
        Assert.Equal(startTime * 1000L, result.RunTimerStartMs);
    }

    [Fact]
    public void GuardedDeltaWithoutTimerField_ReturnsFalse()
    {
        // Top container carrying only field 27 (errCode, int32 scalar) — no timer_info.
        var b = new List<byte>();
        V(b, TagBegin);
        V(b, 16);
        V(b, 27);         // field 27 = errCode (scalar)
        V(b, 0);          // its value
        V(b, TagEnd);

        var ok = DungeonDirtyDataReader.TryReadDirtyBlob(b.ToArray(), out var result);

        Assert.False(ok);
        Assert.False(result.HasTimerInfo);
    }

    [Fact]
    public void GuardedDelta_FlowResultFailed_IsCaptured()
    {
        var b = new List<byte>();
        V(b, TagBegin); V(b, 40);          // top
        V(b, 2);                            // field 2 = flow_info
        V(b, TagBegin); V(b, 16);           //   flow BEGIN
        V(b, 8); V(b, 2);                   //   field 8 = result = 2 (Failed)
        V(b, TagEnd);                       //   flow END
        V(b, TagEnd);                       // top END
        var ok = DungeonDirtyDataReader.TryReadDirtyBlob(b.ToArray(), out var r);
        Assert.True(ok);
        Assert.True(r.HasFlowResult);
        Assert.Equal(2, r.FlowResult);
    }

    [Fact]
    public void GuardedDelta_Settlement_PassTimeAndScore_Captured()
    {
        var b = new List<byte>();
        V(b, TagBegin); V(b, 40);          // top
        V(b, 7);                            // field 7 = settlement
        V(b, TagBegin); V(b, 24);           //   settlement BEGIN
        V(b, 1); V(b, 481);                 //   field 1 = pass_time = 481 (8m1s)
        V(b, 5); V(b, 425);                 //   field 5 = master_mode_score = 425
        V(b, TagEnd);                       //   settlement END
        V(b, TagEnd);                       // top END
        var ok = DungeonDirtyDataReader.TryReadDirtyBlob(b.ToArray(), out var r);
        Assert.True(ok);
        Assert.True(r.HasSettlement);
        Assert.Equal(481, r.PassTimeSeconds);
        Assert.Equal(425, r.MasterModeScore);
    }

    [Fact]
    public void GuardedDelta_SettlementAndTimer_BothSurvive()
    {
        // Regression: timer field assignment must use 'with' to preserve prior settlement fields.
        // Top container carrying both field 7 (settlement) and field 15 (timer_info),
        // in ascending index order (as the game sends them). Both should survive.
        var b = new List<byte>();
        V(b, TagBegin); V(b, 60);           // top
        V(b, 7);                            // field 7 = settlement
        V(b, TagBegin); V(b, 16);           //   settlement BEGIN
        V(b, 1); V(b, 481);                 //   pass_time
        V(b, TagEnd);                       //   settlement END
        V(b, 15);                           // field 15 = timer_info
        V(b, TagBegin); V(b, 16);           //   timer BEGIN
        V(b, 2); V(b, 1783236876);          //   start_time
        V(b, TagEnd);                       //   timer END
        V(b, TagEnd);                       // top END
        var ok = DungeonDirtyDataReader.TryReadDirtyBlob(b.ToArray(), out var r);
        Assert.True(ok);
        Assert.True(r.HasSettlement); Assert.Equal(481, r.PassTimeSeconds);
        Assert.True(r.HasTimerInfo); Assert.Equal(1783236876, r.StartTimeSeconds);
    }
}
