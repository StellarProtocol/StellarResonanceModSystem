using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Protobuf;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// EVENT-DRIVEN capture of the World/scene-scoped attributes the dungeon-state sink consumes —
/// currently <c>AttrDeathCount</c> (348), the settlement "Defeated" counter.
///
/// <para><b>Owner ruling 2026-08-23 (binding):</b> capture is event-driven at the RIGHT probe point;
/// no polling / timer-based data gathering. Until this rework the probe read
/// <c>Panda.ZGame.ZWorld.Instance.GetWorldLuaAttr(348).Value</c> through four layers of IL2CPP
/// reflection on EVERY main-thread framework tick and diffed the result — a compare-poll.</para>
///
/// <para><b>The event.</b> The game itself never polls this attr either: its dungeon HUD binds
/// <c>Z.World:BindWorldLuaAttrWatcher({AttrDeathCount}, refreshDeadUI)</c>
/// (<c>lua/ui/component/dungeon/dungeon_time.lua</c>) and only redraws when the watcher fires. That
/// watcher fires because the world attr collection was re-parsed — <c>ZWorld.ParseAttrProto(AttrCollection)</c>
/// — and the collection's wire carrier is the scene attr sync. So the ARRIVAL of a scene attr
/// collection is the correct signal, and this probe taps it directly on the wire, upstream of the
/// game's own watcher:</para>
/// <list type="bullet">
/// <item><b>WorldNtf 3 <c>EnterScene</c></b> → <c>EnterSceneInfo.SceneAttrs</c>: the scene's attr set at
/// zone-in. This is the SEED — it is what carries an already-accumulated count on a mid-run
/// reconnect, which the old per-tick read used to pick up incidentally.</item>
/// <item><b>WorldNtf 7 <c>SyncSceneAttrs</c></b> → every later change to a scene attr, i.e. every death.</item>
/// </list>
///
/// <para>Both are pure byte-walks on the network receive thread (no IL2CPP object touched anywhere in
/// this file any more, so the whole reflection/fault-disable machinery — <c>ZWorld</c> type lookup,
/// <c>Zproto.ZAttr&lt;int&gt;</c> re-wrapping, the <c>Il2CppObjectBase.Pointer</c> read, the
/// permanent-disable latch and the <c>IClientState</c> handshake guard — is gone). The sink is
/// already written from this thread by <see cref="PandaDungeonProbe"/>, and
/// <c>DungeonStateService.SetDefeated</c> is interlocked.</para>
///
/// <para><b>Consumer semantics are unchanged:</b> only a positive, changed count is latched, and only
/// while a run id is active. <see cref="IDungeonState.LastDefeatedCount"/> keeps reading exactly what
/// it read before. Registration ORDER matters — Host registers this probe AFTER
/// <see cref="PandaCombatStubProbe"/>, so the method-3 seed runs once that probe has already latched
/// the new run id from the very same packet.</para>
///
/// <para>Diagnostics live in <c>PandaWorldAttrProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaWorldAttrProbe
{
    private const string SourceEnterScene = "enter-scene.SceneAttrs";
    private const string SourceSceneSync  = "SyncSceneAttrs(7)";

    private readonly IDungeonStateSink _sink;
    private readonly IDungeonState _state;
    private readonly IPluginLog _log;

    // Last value pushed to the sink; suppresses the steady-state re-delivery of an unchanged count.
    // Zeroed on every enter-scene because the game zeroes its side there too
    // (ZWorld.OnEnterScene -> ResetSceneAttrs) and DungeonStateService clears _lastDefeated on a new
    // run id — without this reset, two consecutive runs that reach the SAME count would leave the
    // second run reporting 0 (the service cleared, the probe still thought it had latched).
    private int _lastDefeated;

    public PandaWorldAttrProbe(IDungeonStateSink sink, IDungeonState state, IPluginLog log)
    {
        _sink  = sink  ?? throw new ArgumentNullException(nameof(sink));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Subscribes the two scene-attr carriers on the shared dispatcher. MUST be registered after
    /// <see cref="PandaCombatStubProbe"/> (see the class doc's ordering note) and before
    /// <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher) => RegisterHandlers(dispatcher.Register);

    /// <summary>The subscription itself, against a bare <c>Register</c> callback so the unit tests can
    /// drive the SAME registration into a plain <see cref="StubRouter"/> (no IL2CPP dispatcher) instead
    /// of restating the method ids and risking drift.</summary>
    internal void RegisterHandlers(Action<uint, Action<uint, byte[]>> register)
    {
        register(WorldNtfMethodIds.EnterScene,     OnEnterScene);   // 3 — seed
        register(WorldNtfMethodIds.SyncSceneAttrs, OnSceneAttrs);   // 7 — every later change
    }

    // WorldNtf 3. The scene changed: drop the memo (see _lastDefeated) and seed from this packet's
    // own scene attrs. A scene with no 348 row simply seeds nothing — the count starts at 0, which is
    // what DungeonStateService already holds for a fresh run.
    internal void OnEnterScene(uint methodId, byte[] payload)
    {
        _lastDefeated = 0;
        if (!EnterSceneReader.TryReadSceneAttrs(payload, out var sceneAttrs, out _)) return;
        LatchDeathCount(sceneAttrs, SourceEnterScene);
    }

    // WorldNtf 7. Every later scene-attr change (weather, day/night, firework timers, AND the death
    // count) rides this one message, carrying only the attrs that changed — so a delivery without a
    // 348 row is the common case and costs one scan of a handful of rows.
    internal void OnSceneAttrs(uint methodId, byte[] payload)
    {
        if (!SyncSceneAttrsReader.TryRead(payload, out var sceneAttrs)) return;
        LatchDeathCount(sceneAttrs, SourceSceneSync);
    }

    private void LatchDeathCount(in AttrCollectionMsg sceneAttrs, string source)
    {
        var items = sceneAttrs.Items;
        if (items is null) return;
        for (int i = 0; i < items.Count; i++)
        {
            var attr = items[i];
            if (attr.Id != AttrTypeIds.AttrDeathCount) continue;
            Latch((int)attr.DecodedLong, source);
            return;
        }
    }

    // Same three gates the per-tick read applied, in the same order: inside an instanced run only,
    // positive only, changed only.
    private void Latch(int value, string source)
    {
        if (_state.CurrentRunId == 0) return;
        if (value <= 0 || value == _lastDefeated) return;
        _lastDefeated = value;
        _sink.SetDefeated(value);
        DiagDefeated(value, source);
    }
}
