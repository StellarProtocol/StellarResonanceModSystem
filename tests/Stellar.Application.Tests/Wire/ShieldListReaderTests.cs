using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// <see cref="ShieldListReader"/> over the <c>AttrShieldList</c> (attr 60050) payload:
/// a repeated shield-entry message whose field 3 is the entry's CURRENT shield; the
/// reader returns the SUM of field 3 across all entries.
/// </summary>
public sealed class ShieldListReaderTests
{
    // One ShieldEntry { f1, f2, cur(=3), max(=4), f5 } as a varint-only sub-message.
    private static byte[] Entry(long f1, long f2, long cur, long max, long f5) => new WireBytes()
        .Tag(1, 0).Varint((ulong)f1)
        .Tag(2, 0).Varint((ulong)f2)
        .Tag(3, 0).Varint((ulong)cur)
        .Tag(4, 0).Varint((ulong)max)
        .Tag(5, 0).Varint((ulong)f5)
        .ToArray();

    private static byte[] List(params byte[][] entries)
    {
        var w = new WireBytes();
        foreach (var e in entries) w.Tag(1, 2).LengthDelimited(e);   // repeated ShieldEntry = 1
        return w.ToArray();
    }

    [Fact]
    public void Decodes_the_verified_real_capture_to_2033871()
    {
        // Exact bytes captured live (task RE): one entry {f1=4, f2=11, f3=2033871, f4=2033871, f5=2033891}.
        // TOTAL current shield = Σ(field 3) = 2033871.
        var payload = new byte[]
        {
            0x0a, 0x10,                         // field 1, wire 2, len 16 → the entry sub-message
            0x08, 0x04,                         // f1 = 4
            0x10, 0x0b,                         // f2 = 11
            0x18, 0xcf, 0x91, 0x7c,             // f3 = 2033871  (current shield)
            0x20, 0xcf, 0x91, 0x7c,             // f4 = 2033871  (max shield)
            0x28, 0xe3, 0x91, 0x7c,             // f5 = 2033891
        };

        Assert.Equal(2033871L, ShieldListReader.Read(payload));
    }

    [Fact]
    public void Empty_payload_returns_zero()
        => Assert.Equal(0L, ShieldListReader.Read(System.ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Two_entries_sum_their_current_shields()
    {
        var payload = List(Entry(1, 2, 1500, 2000, 9), Entry(3, 4, 500, 800, 7));
        Assert.Equal(2000L, ShieldListReader.Read(payload));   // 1500 + 500
    }

    [Fact]
    public void Entry_without_field3_contributes_zero()
    {
        // A slot with no current shield (only f1/f2) still parses; it adds 0.
        var noCur = new WireBytes().Tag(1, 0).Varint(4).Tag(2, 0).Varint(11).ToArray();
        var payload = List(noCur, Entry(0, 0, 777, 900, 0));
        Assert.Equal(777L, ShieldListReader.Read(payload));
    }

    [Fact]
    public void Malformed_payload_does_not_throw_and_returns_zero()
    {
        // tag(field 1, wire 2) then a 5-byte varint declaring int.MaxValue bytes — the
        // length overflow guard must bail without throwing (nothing summed → 0).
        var payload = new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07 };
        Assert.Equal(0L, ShieldListReader.Read(payload));
    }
}
