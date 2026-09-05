using System;
using System.Collections.Generic;
using System.IO;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>Pins the pure half of the P2 attribute-event emission (spec § 6.1): one packet's stored
/// scalars → ONE EntityAttributesChanged for a PLAYER, stamped with the packet's own timestamp; nothing
/// for monsters; nothing for an empty batch; the batch is reusable across packets.</summary>
public sealed class AttrChangeBatchTests
{
    static readonly EntityId Player  = new(0x0000_0001_0000_0280);   // low16 = 640
    static readonly EntityId Monster = new(0x0000_0009_0000_0040);

    [Fact]
    public void Player_batch_flushes_one_event_with_every_pair_and_the_packet_stamp()
    {
        var b = new AttrChangeBatch();
        b.Add(11710, 3350); b.Add(12670, 1200);
        var ev = b.Flush(Player, 1_788_604_960_970L);
        Assert.NotNull(ev);
        Assert.Equal(1_788_604_960_970L, ev!.TimestampMs);
        Assert.Equal(Player, ev.EntityId);
        Assert.Equal(new[] { 11710, 12670 }, new[] { ev.Attrs[0].AttrId, ev.Attrs[1].AttrId });
        Assert.Equal(new[] { 3350L, 1200L }, new[] { ev.Attrs[0].Value, ev.Attrs[1].Value });
    }

    [Fact]
    public void Monster_batch_flushes_nothing_but_still_clears()
    {
        var b = new AttrChangeBatch();
        b.Add(11710, 1);
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
        b.Add(11710, 1);
        var first = b.Flush(Player, 1L)!;
        b.Add(11780, 2);
        var second = b.Flush(Player, 2L)!;
        Assert.Single(first.Attrs);  Assert.Equal(11710, first.Attrs[0].AttrId);
        Assert.Single(second.Attrs); Assert.Equal(11780, second.Attrs[0].AttrId);
    }

    /// <summary>Structural pin (fix round 1): the probe stores a scalar attr in exactly ONE place —
    /// PandaCombatStubProbe.AttrEvents.cs's StoreScalarAttr, which writes the sink and records the pair
    /// together. A bare `_sink.SetEntityAttribute(` anywhere else is an unpaired write: the attr reaches
    /// GetAttributes but is silently missing from that packet's EntityAttributesChanged, so the rDPS
    /// sheet drifts from what the game actually sent. That is exactly the defect this pins — Vitals.cs's
    /// ApplyParsedDelta wrote FightPoint straight to the sink. Route new writes through the chokepoint;
    /// do NOT relax this test to a count.</summary>
    [Fact]
    public void Every_scalar_attr_write_goes_through_the_StoreScalarAttr_chokepoint()
    {
        var probeDir = Path.Combine(RepoRoot(), "src", "Stellar.Infrastructure", "Game");
        var probeFiles = Directory.GetFiles(probeDir, "PandaCombatStubProbe*.cs");
        Assert.NotEmpty(probeFiles);

        var hits = new List<string>();
        foreach (var file in probeFiles)
        {
            var text = File.ReadAllText(file);
            for (int i = text.IndexOf(SinkWrite, StringComparison.Ordinal); i >= 0;
                 i = text.IndexOf(SinkWrite, i + 1, StringComparison.Ordinal))
            {
                hits.Add(Path.GetFileName(file));
            }
        }

        Assert.Equal(new[] { "PandaCombatStubProbe.AttrEvents.cs" }, hits.ToArray());
    }

    const string SinkWrite = "_sink.SetEntityAttribute(";

    /// <summary>Walks up from the test binary to the framework repo root (the dir holding src/Stellar.sln).</summary>
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "Stellar.sln"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException($"No ancestor of '{AppContext.BaseDirectory}' contains src/Stellar.sln.");
        return dir.FullName;
    }
}
