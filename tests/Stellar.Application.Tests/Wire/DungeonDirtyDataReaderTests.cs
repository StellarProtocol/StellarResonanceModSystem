using System;
using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Covers <see cref="DungeonDirtyDataReader"/> against hand-built payloads
/// mirroring the recon'd framing: the protobuf envelope
/// <c>SyncDungeonDirtyData{ BufferStream v_data=1 { bytes buffer=1 } }</c>
/// around the game's int32-LE container-merge blob
/// (<c>lua/zcontainer/dungeon_sync_data.lua MergeData</c>):
/// <c>[-2][size][fieldIndex payload]…[-3]</c>, where <c>size</c> spans the
/// entry list only (the trailing <c>-3</c> sits outside it) and an empty
/// container is the 8-byte <c>[-2][-3]</c> form. DungeonSyncData field 15 =
/// timerInfo, whose fields are all int32 scalars; timer field 2 = start_time
/// (epoch seconds).
/// </summary>
public sealed class DungeonDirtyDataReaderTests
{
    private const int TagBegin = -2;
    private const int TagEnd = -3;

    [Fact]
    public void TimerOnlyDelta_NoSceneUuid_ExtractsStartTime()
    {
        // The realistic mid-run delta: ONLY field 15 changed — no field 1. This
        // is exactly the shape the strict method-23 reader would reject.
        var blob = Container(
            Field(15, Container(
                Field(2, I32(1_750_000_000)),      // start_time
                Field(4, I32(2)),                  // direction
                Field(9, I32(0)))));               // pause_total_time

        Assert.True(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out var result));
        Assert.True(result.HasTimerInfo);
        Assert.Equal(1_750_000_000, result.StartTimeSeconds);
        Assert.Equal(1_750_000_000_000L, result.RunTimerStartMs);
        Assert.Equal(2, result.Direction);
        Assert.Equal(0, result.PauseTotalTime);
    }

    [Fact]
    public void MixedDelta_SceneUuidAndOtherContainersSkipped_TimerStillParsed()
    {
        var blob = Container(
            Field(1, I64(746498365818142720L)),                    // sceneUuid (int64 scalar)
            Field(2, Container(Field(1, I32(3)), Field(2, I32(7)))), // flowInfo — skipped container
            Field(10, EmptyContainer()),                            // dungeonVar — empty [-2][-3] form
            Field(15, Container(Field(2, I32(123_456)), Field(3, I32(900)))),
            Field(27, I32(0)));                                     // errCode (int32 scalar)

        Assert.True(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out var result));
        Assert.Equal(123_456, result.StartTimeSeconds);
        Assert.Equal(900, result.DungeonTimes);
    }

    [Fact]
    public void DeltaWithoutTimerField_ReturnsFalse()
    {
        var blob = Container(
            Field(5, Container(Field(1, I32(42)))),   // damage delta only
            Field(27, I32(0)));

        Assert.False(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out _));
    }

    [Fact]
    public void EmptyTimerContainer_ReportsPresentButZeroStart()
    {
        var blob = Container(Field(15, EmptyContainer()));

        Assert.True(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out var result));
        Assert.True(result.HasTimerInfo);
        Assert.Equal(0, result.StartTimeSeconds);
    }

    [Fact]
    public void UnknownFutureTimerField_RecoversLikeTheGame_AndKeepsEarlierCapture()
    {
        // Timer entries: start_time then an unknown index (12) — the reader must
        // jump to the container's end (entriesStart+size) and read the end tag,
        // mirroring lua's SetOffset(offset+size) recovery.
        var blob = Container(
            Field(15, Container(
                Field(2, I32(555)),
                Field(12, I32(999)))));   // unknown per current dungeon_timer_info.lua

        Assert.True(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out var result));
        Assert.Equal(555, result.StartTimeSeconds);
    }

    [Fact]
    public void UnknownFutureTopLevelField_RecoversAndReportsTimerSeenBefore()
    {
        var blob = Container(
            Field(15, Container(Field(2, I32(777)))),
            Field(28, I32(1)));   // beyond errCode(27) — unknown future field

        Assert.True(DungeonDirtyDataReader.TryReadTimerStart(Wrap(blob), out var result));
        Assert.Equal(777, result.StartTimeSeconds);
    }

    [Theory]
    [InlineData(new byte[0])]                          // empty
    [InlineData(new byte[] { 1, 2, 3 })]               // shorter than one int32
    public void MalformedBlob_ReturnsFalse(byte[] blob)
        => Assert.False(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out _));

    [Fact]
    public void WrongBeginTag_ReturnsFalse()
    {
        var blob = Bytes(I32(7), I32(4), I32(TagEnd));
        Assert.False(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out _));
    }

    [Fact]
    public void EmptyDelta_MinusThreeSizeSentinel_ReturnsFalse()
        => Assert.False(DungeonDirtyDataReader.TryReadDirtyBlob(Bytes(I32(TagBegin), I32(TagEnd)), out _));

    [Fact]
    public void TruncatedTimerContainer_ReturnsFalse()
    {
        // Declared size exceeds the remaining bytes.
        var blob = Bytes(I32(TagBegin), I32(64), I32(15), I32(TagBegin), I32(400));
        Assert.False(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out _));
    }

    [Fact]
    public void SizeLargerThanPayload_ReturnsFalse()
    {
        var blob = Bytes(I32(TagBegin), I32(9999), I32(TagEnd));
        Assert.False(DungeonDirtyDataReader.TryReadDirtyBlob(blob, out _));
    }

    [Fact]
    public void NonProtobufEnvelope_ReturnsFalse()
        => Assert.False(DungeonDirtyDataReader.TryReadTimerStart(new byte[] { 0xFF, 0xFF }, out _));

    // ---- payload builders -------------------------------------------------

    // Full container framing: [-2][size][entries…][-3], size = entry bytes only.
    private static byte[] Container(params byte[][] entries)
    {
        var body = Bytes(entries);
        return Bytes(I32(TagBegin), I32(body.Length), body, I32(TagEnd));
    }

    // Empty container short form: [-2][-3].
    private static byte[] EmptyContainer() => Bytes(I32(TagBegin), I32(TagEnd));

    private static byte[] Field(int index, byte[] payload) => Bytes(I32(index), payload);

    private static byte[] I32(int v) => BitConverter.GetBytes(v);

    private static byte[] I64(long v) => BitConverter.GetBytes(v);

    private static byte[] Bytes(params byte[][] parts)
    {
        var b = new List<byte>();
        foreach (var p in parts) b.AddRange(p);
        return b.ToArray();
    }

    // Protobuf envelope: SyncDungeonDirtyData{ v_data=1 } → BufferStream{ buffer=1 }.
    private static byte[] Wrap(byte[] blob) => LenDelim(1, LenDelim(1, blob));

    private static byte[] LenDelim(int field, byte[] payload)
    {
        var b = new List<byte> { (byte)((field << 3) | 2) };
        ulong len = (ulong)payload.Length;
        while (len >= 0x80) { b.Add((byte)(len | 0x80)); len >>= 7; }
        b.Add((byte)len);
        b.AddRange(payload);
        return b.ToArray();
    }
}
