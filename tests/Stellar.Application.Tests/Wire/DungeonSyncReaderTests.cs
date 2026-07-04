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
