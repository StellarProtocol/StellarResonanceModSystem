using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Combat;

/// <summary>
/// Covers the crash-safe deferred Lua-tap machinery in
/// <see cref="PandaDungeonProbe"/> (the <c>Deferred</c> partial) through its
/// test seam: the enqueue callback (<c>OnWorldNtfLua</c>, internal for these
/// tests) plus <see cref="PandaDungeonProbe.DrainDeferred"/>, observed via a
/// real <see cref="DungeonStateService"/>. Two hardening invariants:
/// <list type="number">
/// <item>Run-id scoping — an item enqueued under one run id and drained under
/// another (scene change while the tick was gated) must NOT write into the new
/// run's state; skipped-stale items surface on the drain diag.</item>
/// <item>Count integrity — a throw inside the enqueue callback (e.g. from the
/// arrival-clock read) must not leak <c>_deferredCount</c> upward, or the
/// bounded queue wedges permanently at "full".</item>
/// </list>
/// </summary>
public sealed class PandaDungeonProbeDeferredTests
{
    // Instanced-dungeon snowflake-magnitude run ids (see DungeonRunIdGateTests).
    private const long RunA = 493733355695636480L;
    private const long RunB = 493733355695636481L;

    // Mirrors PandaDungeonProbe.DeferredCap (private const). If the cap
    // changes, the leak test below just becomes weaker, not wrong — it enqueues
    // cap-many poisoned attempts to prove none of them consumed a slot.
    private const int DeferredCap = 64;

    private readonly DungeonStateService _service = new();
    private readonly ThrowableClockCombat _combat = new();
    private readonly StubLog _log = new();
    private readonly PandaDungeonProbe _probe;

    public PandaDungeonProbeDeferredTests()
        => _probe = new PandaDungeonProbe(_service, _service, _combat, _log);

    [Fact]
    public void Drain_SameRun_LatchesMethod55ArrivalEdge()
    {
        _service.SetCurrentRun(RunA);
        _combat.ServerNowMs = 111_000;

        _probe.OnWorldNtfLua(WorldNtfMethodIds.NotifyStartPlayingDungeon, Array.Empty<byte>());
        _probe.DrainDeferred();

        Assert.Equal(111_000, ((IDungeonState)_service).RunTimerStartMs);
        Assert.DoesNotContain(_log.InfoLines, l => l.Contains("skipped as stale"));
    }

    [Fact]
    public void Drain_RunChangedBetweenEnqueueAndDrain_SkipsTimerLatch_AndCountsStale()
    {
        _service.SetCurrentRun(RunA);
        _combat.ServerNowMs = 111_000;
        _probe.OnWorldNtfLua(WorldNtfMethodIds.NotifyStartPlayingDungeon, Array.Empty<byte>());

        // Scene change before the gated tick drains: a NEW run begins.
        _service.SetCurrentRun(RunB);
        _probe.DrainDeferred();

        // The stale method-55 edge must NOT latch the new run's timer.
        Assert.Equal(0, ((IDungeonState)_service).RunTimerStartMs);
        // …and the skip is surfaced on the drain diag (counter plateau line).
        Assert.Contains(_log.InfoLines, l => l.Contains("skipped as stale") && l.Contains("1 so far"));
    }

    [Fact]
    public void Drain_RunChangedBetweenEnqueueAndDrain_SkipsSettlementWrite()
    {
        _service.SetCurrentRun(RunA);
        _combat.ServerNowMs = 111_000;
        _probe.OnWorldNtfLua(WorldNtfMethodIds.SyncDungeonData, SettlementPayload(passTime: 372, score: 8800));

        _service.SetCurrentRun(RunB);
        _probe.DrainDeferred();

        Assert.Null(((IDungeonState)_service).LastSettlement);
        Assert.Contains(_log.InfoLines, l => l.Contains("skipped as stale"));
    }

    [Fact]
    public void Enqueue_ThrowingClockCapture_DoesNotLeakCount_QueueStaysUsable()
    {
        _service.SetCurrentRun(RunA);

        // Cap-many enqueue attempts that all throw inside the callback (the
        // arrival-clock read). With the pre-fix increment ordering each throw
        // leaked _deferredCount by one, wedging the queue at "full" — every
        // later delivery would be dropped forever.
        _combat.ThrowOnClockRead = true;
        for (int i = 0; i < DeferredCap; i++)
            _probe.OnWorldNtfLua(WorldNtfMethodIds.NotifyStartPlayingDungeon, Array.Empty<byte>());

        // A healthy delivery afterwards must still get a slot and latch.
        _combat.ThrowOnClockRead = false;
        _combat.ServerNowMs = 222_000;
        _probe.OnWorldNtfLua(WorldNtfMethodIds.NotifyStartPlayingDungeon, Array.Empty<byte>());
        _probe.DrainDeferred();

        Assert.Equal(222_000, ((IDungeonState)_service).RunTimerStartMs);
        Assert.DoesNotContain(_log.WarningLines, l => l.Contains("queue overflow"));
    }

    // DungeonSyncData { scene_uuid(1), settlement(7) = { pass_time(1), master_mode_score(5) } }
    // wrapped in SyncDungeonData { v_data(1) } — same hand-built protobuf shape
    // as DungeonSyncReaderTests.
    private static byte[] SettlementPayload(long passTime, long score)
    {
        var settlement = Msg(Varint(1, passTime), Varint(5, score));
        var data = Msg(Varint(1, RunA), LenDelim(7, settlement));
        return LenDelim(1, data);
    }

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
        while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }

    /// <summary>
    /// Minimal <see cref="ICombatSnapshot"/> whose server-clock read can be
    /// armed to throw — the seam for the count-leak invariant (the probe's
    /// enqueue callback reads <c>ServerNowMs</c> before enqueuing).
    /// </summary>
    private sealed class ThrowableClockCombat : ICombatSnapshot
    {
        private long _serverNowMs;

        public bool ThrowOnClockRead { get; set; }

        public bool IsAvailable => true;
        public EntityId LocalEntityId => EntityId.None;
        public IReadOnlyList<SkillCooldown> LocalCooldowns => Array.Empty<SkillCooldown>();
        public IReadOnlyList<ActiveBuff> LocalBuffs => Array.Empty<ActiveBuff>();
        public IReadOnlyList<CombatEvent> RecentEvents => Array.Empty<CombatEvent>();

        public long ServerNowMs
        {
            get => ThrowOnClockRead
                ? throw new InvalidOperationException("armed: clock read threw")
                : _serverNowMs;
            set => _serverNowMs = value;
        }
    }
}
