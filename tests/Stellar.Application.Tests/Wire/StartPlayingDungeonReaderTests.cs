using System.Collections.Generic;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Unit tests for <see cref="StartPlayingDungeonReader"/> — the minimal
/// structural parser for <c>WorldNtf.NotifyStartPlayingDungeon</c> (method 55).
/// Payloads are hand-built protobuf bytes; no IL2CPP / BepInEx / Unity
/// dependencies.
/// </summary>
public sealed class StartPlayingDungeonReaderTests
{
    private const long CharId = 148061897948001234L;

    [Fact]
    public void Reads_char_id_from_full_payload()
    {
        // StartPlayingDungeonParam { char_id(1)=..., is_use_key(2)=true }
        var param = Msg(
            Varint(1, CharId),
            Varint(2, 1));

        // NotifyStartPlayingDungeon { v_param(1) = StartPlayingDungeonParam }
        var body = LenDelim(1, param);

        Assert.True(StartPlayingDungeonReader.TryReadCharId(body, out var charId));
        Assert.Equal(CharId, charId);
    }

    [Fact]
    public void Reads_char_id_when_preceded_by_unknown_fields()
    {
        // Unknown trailing/leading fields must be skipped, not rejected.
        var param = Msg(
            Varint(3, 7),                                  // unknown varint
            LenDelim(4, new byte[] { 0xAA }),              // unknown sub-message
            Varint(1, CharId));
        var body = LenDelim(1, param);

        Assert.True(StartPlayingDungeonReader.TryReadCharId(body, out var charId));
        Assert.Equal(CharId, charId);
    }

    [Fact]
    public void MissingCharId_ParsesWithZero()
    {
        // Structurally valid param without char_id — acceptable (the value is
        // diagnostic-only; the ARRIVAL is what the consumer stamps).
        var param = Msg(Varint(2, 1));                     // is_use_key only
        var body = LenDelim(1, param);

        Assert.True(StartPlayingDungeonReader.TryReadCharId(body, out var charId));
        Assert.Equal(0L, charId);
    }

    [Fact]
    public void Rejects_empty_payload()
    {
        Assert.False(StartPlayingDungeonReader.TryReadCharId(System.Array.Empty<byte>(), out _));
    }

    [Fact]
    public void Rejects_truncated_param()
    {
        // v_param envelope declares more bytes than present → structural reject.
        var body = new byte[] { 0x0A, 0x05, 0x08 };        // len=5, only 1 byte follows
        Assert.False(StartPlayingDungeonReader.TryReadCharId(body, out _));
    }

    // ── minimal protobuf writers (mirrors DungeonSyncReaderTests) ───────────

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
