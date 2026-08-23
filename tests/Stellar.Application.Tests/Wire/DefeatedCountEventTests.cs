using System;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// PINS the EVENT-DRIVEN Defeated capture (owner ruling 2026-08-23: capture is event-driven at the
/// RIGHT probe point; no polling / timer-based data gathering).
///
/// <para><b>What changed.</b> <c>AttrDeathCount</c> (348) used to be read every main-thread framework
/// tick off <c>Panda.ZGame.ZWorld.Instance.GetWorldLuaAttr(348).Value</c> and diffed — a compare-poll.
/// It is a SCENE attribute (<c>enum_e_attr_type.proto</c> puts it in the 340-351 scene band, between
/// <c>AttrFireworkStartTimeSeconds</c> 347 and <c>AttrDeathSubTimeSecond</c> 349) and rides the scene
/// attr collection on the wire: <c>EnterSceneInfo.SceneAttrs</c> at zone-in (WorldNtf 3) and
/// <c>WorldNtf.SyncSceneAttrs</c> (7) for every later change — the same collection
/// <c>ZWorld.ParseAttrProto</c> fills and the game's own dungeon HUD watches through
/// <c>Z.World:BindWorldLuaAttrWatcher({AttrDeathCount}, …)</c>.</para>
///
/// <para>Every fixture below is built by the independent <see cref="WireBytes"/> encoder, so these
/// drive the real reader chain (<c>SyncSceneAttrsReader</c> / <c>EnterSceneReader</c> →
/// <c>AttrCollectionReader</c>) with bytes it did not produce: wire-bytes in, Defeated flip out.</para>
/// </summary>
public sealed class DefeatedCountEventTests
{
    private const uint EnterScene     = WorldNtfMethodIds.EnterScene;       // 3
    private const uint SyncSceneAttrs = WorldNtfMethodIds.SyncSceneAttrs;   // 7

    // AttrCollection { int64 Uuid = 1; repeated Attr Attrs = 2 }  /  Attr { int32 Id = 1; bytes RawData = 2 }
    private static byte[] AttrCollection(params (int Id, ulong Value)[] rows)
    {
        var col = new WireBytes();
        foreach (var (id, value) in rows)
        {
            var attr = new WireBytes().Tag(1, 0).Varint((ulong)(uint)id)
                                      .Tag(2, 2).LengthDelimited(new WireBytes().Varint(value).ToArray());
            col.Tag(2, 2).LengthDelimited(attr.ToArray());
        }
        return col.ToArray();
    }

    // WorldNtf.SyncSceneAttrs { AttrCollection attrs = 1 }
    private static byte[] SceneAttrsPacket(params (int Id, ulong Value)[] rows)
        => new WireBytes().Tag(1, 2).LengthDelimited(AttrCollection(rows)).ToArray();

    // WorldNtf.EnterScene { EnterSceneInfo EnterSceneInfo = 1 { AttrCollection SceneAttrs = 1 } }
    private static byte[] EnterScenePacket(params (int Id, ulong Value)[] rows)
    {
        var sceneInfo = new WireBytes().Tag(1, 2).LengthDelimited(AttrCollection(rows)).ToArray();
        return new WireBytes().Tag(1, 2).LengthDelimited(sceneInfo).ToArray();
    }

    private static (PandaWorldAttrProbe Probe, DungeonStateService State) InRun(long runId = 643789110607085568L)
    {
        var state = new DungeonStateService();
        state.SetCurrentRun(runId);
        return (new PandaWorldAttrProbe(state, state, new StubLog()), state);
    }

    [Fact]
    public void SyncSceneAttrs_carrying_attr348_flips_the_Defeated_count()
    {
        var (probe, state) = InRun();
        Assert.Equal(0, state.LastDefeatedCount);

        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 3)));

        Assert.Equal(3, state.LastDefeatedCount);
    }

    [Fact]
    public void SyncSceneAttrs_tracks_each_new_count_and_ignores_a_resend()
    {
        var (probe, state) = InRun();

        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 1)));
        Assert.Equal(1, state.LastDefeatedCount);

        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 2)));
        Assert.Equal(2, state.LastDefeatedCount);

        // A steady-state re-delivery of the SAME value must not re-push (the old poll's diff gate).
        state.SetDefeated(99);
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 2)));
        Assert.Equal(99, state.LastDefeatedCount);
    }

    [Fact]
    public void A_scene_attr_sync_without_attr348_changes_nothing()
    {
        var (probe, state) = InRun();
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 4)));

        // Weather / day-night / firework rows share this message; none of them may disturb the latch.
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((344, 7), (346, 1), (347, 1783196889)));

        Assert.Equal(4, state.LastDefeatedCount);
    }

    [Fact]
    public void EnterScene_SceneAttrs_seeds_an_already_accumulated_count()
    {
        // The mid-run reconnect case the per-tick ZWorld read used to cover incidentally: the count
        // is already at 5 when the client zones in, and no further death may ever arrive.
        var (probe, state) = InRun();

        probe.OnEnterScene(EnterScene, EnterScenePacket(
            (AttrTypeIds.AttrSceneUuid, 643789110607085568UL),
            (AttrTypeIds.AttrDeathCount, 5)));

        Assert.Equal(5, state.LastDefeatedCount);
    }

    [Fact]
    public void EnterScene_clears_the_memo_so_two_runs_reaching_the_same_count_both_report_it()
    {
        // Regression the rework also closes: DungeonStateService zeroes _lastDefeated on a new run id,
        // so a probe-side memo that survived the scene change would suppress the identical count in
        // run 2 and leave it reading 0 forever.
        var (probe, state) = InRun(runId: 100);
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 2)));
        Assert.Equal(2, state.LastDefeatedCount);

        state.SetCurrentRun(200);                       // next dungeon — service latch cleared
        Assert.Equal(0, state.LastDefeatedCount);
        probe.OnEnterScene(EnterScene, EnterScenePacket((AttrTypeIds.AttrSceneUuid, 200)));

        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 2)));
        Assert.Equal(2, state.LastDefeatedCount);
    }

    [Fact]
    public void Outside_a_run_the_count_is_never_latched()
    {
        var state = new DungeonStateService();          // CurrentRunId == 0 (town / open world)
        var probe = new PandaWorldAttrProbe(state, state, new StubLog());

        probe.OnEnterScene(EnterScene, EnterScenePacket((AttrTypeIds.AttrDeathCount, 6)));
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 6)));

        Assert.Equal(0, state.LastDefeatedCount);
    }

    [Fact]
    public void Malformed_or_empty_payloads_are_ignored_without_throwing()
    {
        var (probe, state) = InRun();
        probe.OnSceneAttrs(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 8)));
        Assert.Equal(8, state.LastDefeatedCount);

        probe.OnSceneAttrs(SyncSceneAttrs, Array.Empty<byte>());
        probe.OnSceneAttrs(SyncSceneAttrs, new byte[] { 0x0A, 0x7F });           // length past the end
        probe.OnSceneAttrs(SyncSceneAttrs, new byte[] { 0xFF, 0xFF, 0xFF });     // truncated varint tag
        probe.OnEnterScene(EnterScene, new byte[] { 0x0A, 0x7F });

        Assert.Equal(8, state.LastDefeatedCount);       // latch untouched, nothing thrown
    }

    [Fact]
    public void The_probe_reaches_the_sink_through_the_shared_router_at_methods_3_and_7()
    {
        // End-to-end through the real StubRouter, and with a co-registered method-3 handler standing in
        // for PandaCombatStubProbe's run-id latch: BOTH must run, in registration order, or the seed
        // would apply its run-id gate before the id for this very packet exists.
        var state = new DungeonStateService();
        var probe = new PandaWorldAttrProbe(state, state, new StubLog());
        var router = new StubRouter();

        router.Register(EnterScene, (_, payload) =>
        {
            Assert.True(Protobuf_TryReadSceneUuid(payload, out var uuid));
            state.SetCurrentRun(uuid);                  // stands in for LatchDungeonRunId
        });
        probe.RegisterHandlers(router.Register);        // the SAME subscription RegisterWith performs

        Assert.True(router.Subscribes(EnterScene));
        Assert.True(router.Subscribes(SyncSceneAttrs));

        router.Route(EnterScene, EnterScenePacket(
            (AttrTypeIds.AttrSceneUuid, 643789110607085568UL),
            (AttrTypeIds.AttrDeathCount, 2)));
        Assert.Equal(643789110607085568L, state.CurrentRunId);
        Assert.Equal(2, state.LastDefeatedCount);       // the seed saw the id the earlier handler latched

        router.Route(SyncSceneAttrs, SceneAttrsPacket((AttrTypeIds.AttrDeathCount, 9)));
        Assert.Equal(9, state.LastDefeatedCount);
    }

    private static bool Protobuf_TryReadSceneUuid(byte[] payload, out long sceneUuid)
        => Stellar.Infrastructure.Game.Protobuf.EnterSceneReader.TryReadSceneId(payload, out sceneUuid);
}
