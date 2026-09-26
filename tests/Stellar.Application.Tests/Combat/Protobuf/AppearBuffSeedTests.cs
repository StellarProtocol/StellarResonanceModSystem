using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// Review fix D (m4, 2026-09-26): the probe hands an appear's buff snapshot to the sink ONLY when the reader
/// decoded it completely. An "unknown" (malformed/truncated) snapshot skips the replace, so buffs the client
/// could not read are never wiped.
/// </summary>
public sealed class AppearBuffSeedTests
{
    private sealed class RecordingSink : ICombatBuffSink
    {
        public readonly List<(EntityId, IReadOnlyList<ActiveBuff>?)> Replaced = new();
        public void SetLocalCooldowns(IReadOnlyList<SkillCooldown> cooldowns) { }
        public void ApplyBuffEvents(EntityId entityId, IReadOnlyList<ActiveBuff> upserts, IReadOnlyList<int> removedBuffUuids, long timestampMs) { }
        public void ClearAllBuffs() { }
        public void ReplaceEntityBuffs(EntityId entityId, IReadOnlyList<ActiveBuff>? buffs, long timestampMs) => Replaced.Add((entityId, buffs));
    }

    private static readonly EntityId Id = new((1L << 16) | 640L);

    [Fact]
    public void UnknownSnapshot_SkipsReplace()
    {
        var sink = new RecordingSink();
        var seeded = AppearBuffSeed.Apply(sink, Id, new AppearEntityMsg(Id.Value, null, null, BuffsUnknown: true), 1000);
        Assert.False(seeded);
        Assert.Empty(sink.Replaced);
    }

    [Fact]
    public void CompleteSnapshot_Replaces()
    {
        var sink = new RecordingSink();
        var buffs = new[] { new ActiveBuff(1, 2202110, 1, Id, 1, 1, 0, 0, 6, 510) };
        Assert.True(AppearBuffSeed.Apply(sink, Id, new AppearEntityMsg(Id.Value, null, buffs), 1000));
        var (eid, list) = Assert.Single(sink.Replaced);
        Assert.Equal(Id, eid);
        Assert.Same(buffs, list);
    }

    [Fact]
    public void AbsentField7_ReplacesWithEmpty()
    {
        var sink = new RecordingSink();
        Assert.True(AppearBuffSeed.Apply(sink, Id, new AppearEntityMsg(Id.Value, null), 1000));
        Assert.Null(Assert.Single(sink.Replaced).Item2);
    }
}
