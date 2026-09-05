using System;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>Pins the pure half of the P2 attribute-event emission (spec § 6.1): one packet's stored
/// scalars → ONE EntityAttributesChanged for a PLAYER, stamped with the packet's own timestamp; nothing
/// for monsters; nothing for an empty batch; the batch is reusable across packets; the batch is
/// entity-keyed so a leaked batch from an interrupted packet is never mis-attributed; and
/// <see cref="AttrChangeBatch.IsStorableScalar"/> admits a genuine varint zero while still skipping junk
/// non-varint payloads that also decode to zero.</summary>
public sealed class AttrChangeBatchTests
{
    static readonly EntityId Player  = new(0x0000_0001_0000_0280);   // low16 = 640
    static readonly EntityId OtherPlayer = new(0x0000_0002_0000_0280);
    static readonly EntityId Monster = new(0x0000_0009_0000_0040);

    [Fact]
    public void Player_batch_flushes_one_event_with_every_pair_and_the_packet_stamp()
    {
        var b = new AttrChangeBatch();
        b.Add(Player, 11710, 3350); b.Add(Player, 12670, 1200);
        var ev = b.Flush(Player, 1_788_604_960_970L);
        Assert.NotNull(ev);
        Assert.Equal(1_788_604_960_970L, ev!.TimestampMs);
        Assert.Equal(Player, ev.TargetId);
        Assert.Equal(new[] { 11710, 12670 }, new[] { ev.Attrs[0].AttrId, ev.Attrs[1].AttrId });
        Assert.Equal(new[] { 3350L, 1200L }, new[] { ev.Attrs[0].Value, ev.Attrs[1].Value });
    }

    [Fact]
    public void Monster_batch_flushes_nothing_but_still_clears()
    {
        var b = new AttrChangeBatch();
        b.Add(Monster, 11710, 1);
        Assert.Null(b.Flush(Monster, 5L));
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void Empty_batch_flushes_nothing()
    {
        Assert.Null(new AttrChangeBatch().Flush(Player, 5L));
    }

    [Fact]
    public void Flush_clears_so_the_next_packet_starts_empty_and_the_emitted_list_is_a_snapshot()
    {
        var b = new AttrChangeBatch();
        b.Add(Player, 11710, 1);
        var first = b.Flush(Player, 1L)!;
        b.Add(Player, 11780, 2);
        var second = b.Flush(Player, 2L)!;
        Assert.Single(first.Attrs);  Assert.Equal(11710, first.Attrs[0].AttrId);
        Assert.Single(second.Attrs); Assert.Equal(11780, second.Attrs[0].AttrId);
    }

    /// <summary>Fix round 2: a batch left holding pairs for entity A (its Flush(A,...) was never reached —
    /// an interrupted packet) must not bleed into entity B's packet. The next Add for a DIFFERENT entity
    /// discards the leaked pairs rather than mis-attributing them.</summary>
    [Fact]
    public void A_leaked_batch_from_another_entity_is_discarded_on_the_next_add()
    {
        var b = new AttrChangeBatch();
        b.Add(Player, 11710, 1);
        b.Add(OtherPlayer, 22000, 2);
        var ev = b.Flush(OtherPlayer, 9L)!;
        Assert.Single(ev.Attrs);
        Assert.Equal(22000, ev.Attrs[0].AttrId);
        Assert.Equal(2L, ev.Attrs[0].Value);
    }

    /// <summary>Flushing with an entity id that does not match the batch's own (leaked) entity returns null
    /// and still clears — the leaked pairs never surface under the wrong id.</summary>
    [Fact]
    public void Flush_for_a_different_entity_than_the_batch_returns_null_and_clears()
    {
        var b = new AttrChangeBatch();
        b.Add(Player, 11710, 1);
        Assert.Null(b.Flush(OtherPlayer, 9L));
        Assert.Equal(0, b.Count);
    }

    [Fact]
    public void IsStorableScalar_stores_any_nonzero_decode()
    {
        Assert.True(AttrChangeBatch.IsStorableScalar(5, new byte[] { 0x05 }));
        Assert.True(AttrChangeBatch.IsStorableScalar(5, Array.Empty<byte>()));
    }

    [Fact]
    public void IsStorableScalar_stores_a_genuine_single_byte_varint_zero()
    {
        Assert.True(AttrChangeBatch.IsStorableScalar(0, new byte[] { 0x00 }));
    }

    [Fact]
    public void IsStorableScalar_skips_a_zero_decode_from_an_empty_payload()
    {
        Assert.False(AttrChangeBatch.IsStorableScalar(0, Array.Empty<byte>()));
    }

    [Fact]
    public void IsStorableScalar_skips_a_zero_decode_from_a_two_byte_payload()
    {
        Assert.False(AttrChangeBatch.IsStorableScalar(0, new byte[] { 0x00, 0x00 }));
    }

    [Fact]
    public void IsStorableScalar_skips_a_zero_decode_from_a_string_payload()
    {
        Assert.False(AttrChangeBatch.IsStorableScalar(0, System.Text.Encoding.UTF8.GetBytes("abc")));
    }
}
