using System;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Combat probe that sources WorldNtf bytes from the
/// <see cref="WorldNtfStubDispatcher"/> — the single-owner postfix that reads
/// the stub header once with cached accessors and routes subscribed method IDs.
///
/// <para>
/// This probe no longer installs its own HarmonyX hook. Call
/// <see cref="RegisterWith"/> to subscribe the four wired WorldNtf method IDs
/// to the shared dispatcher, then let the dispatcher's <c>Install</c> activate
/// the hook. Wiring is performed by <c>BootstrapPlugin</c> (Task 5).
/// </para>
///
/// <para>
/// First-occurrence diagnostic logging for unfamiliar method IDs now lives in
/// the dispatcher. This file retains the per-session sanity caps for damage
/// and entity-attr events.
/// </para>
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    private readonly ICombatEventSink      _sink;
    private readonly DungeonRunIdResolver  _runIdResolver;
    private readonly WireEntityPositions   _positions;
    private readonly IPluginLog            _log;
    // Native boss-vitals cache: OnEnterScene calls _entityVitals.Reset() to bound it to one scene's
    // lifetime (I1 review fix), mirroring _sink.ResetEntities()/_positions.Clear() just above it —
    // a real lifecycle dependency, not diagnostics-only. Also used by DiagEntityLife's uuid/event/
    // disappearType trace (PandaCombatStubProbe.Diagnostics.cs), which — after the C2 review fix —
    // never calls back into it (that trace no longer touches EntityVitalsService at all; it stays
    // main-thread-only, never invoked from this probe's network-receive-thread handlers).
    private readonly EntityVitalsService   _entityVitals;

    /// <summary>
    /// Cached local entity uuid. Set when <see cref="OnSelfDelta"/> first
    /// observes a non-zero <c>AoiSyncToMeDelta.Uuid</c>; used by
    /// <see cref="OnNearDelta"/> to suppress duplicate buff diffs for self.
    /// Volatile because writers run on the network receive thread.
    /// </summary>
    private long _localEntityIdValue;

    /// <summary>
    /// Session-wide counter of damage-fanout log lines emitted by
    /// <see cref="ProcessDeltas"/>. Capped at <see cref="DamageLogCap"/> — a
    /// sanity check that the SkillEffects.Damages[] path is live. The
    /// <c>STELLAR_DIAGNOSTICS=1</c> mode adds 95 more events via a separate
    /// counter in <see cref="PandaCombatStubProbe.Diagnostics"/>.
    /// </summary>
    private int _damageLogCount;
    private const int DamageLogCap = 5;

    public PandaCombatStubProbe(
        ICombatEventSink sink,
        DungeonRunIdResolver runIdResolver,
        WireEntityPositions positions,
        IPluginLog log,
        EntityVitalsService entityVitals)
    {
        _sink          = sink          ?? throw new ArgumentNullException(nameof(sink));
        _runIdResolver = runIdResolver ?? throw new ArgumentNullException(nameof(runIdResolver));
        _positions     = positions     ?? throw new ArgumentNullException(nameof(positions));
        _log           = log           ?? throw new ArgumentNullException(nameof(log));
        _entityVitals  = entityVitals  ?? throw new ArgumentNullException(nameof(entityVitals));
    }

    /// <summary>Clear the cached local entity uuid on logout so the next account doesn't inherit the
    /// previous player's self-uuid (used by <see cref="OnNearDelta"/> to suppress duplicate self buff
    /// diffs). Mirrors the CombatService session reset; called by the Host OnLogout dispatcher.</summary>
    internal void ResetLocalEntityId() => _localEntityIdValue = 0;

    /// <summary>
    /// Subscribes the four wired WorldNtf method IDs to the shared dispatcher.
    /// Must be called before <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.EnterScene,       Dispatch);
        dispatcher.Register(WorldNtfMethodIds.SyncServerTime,    Dispatch);
        dispatcher.Register(WorldNtfMethodIds.SyncNearEntities,  Dispatch);
        dispatcher.Register(WorldNtfMethodIds.SyncNearDeltaInfo, Dispatch);
        dispatcher.Register(WorldNtfMethodIds.SyncToMeDeltaInfo, Dispatch);
    }

    /// <summary>
    /// Route a WorldNtf payload to the matching handler. Called by the
    /// dispatcher after it has confirmed uuid==WorldNtf and the method ID is
    /// subscribed. Signature matches <c>Action&lt;uint, byte[]&gt;</c> so it
    /// can be passed as a method group to
    /// <see cref="WorldNtfStubDispatcher.Register"/>.
    /// </summary>
    private void Dispatch(uint methodId, byte[] payload)
    {
        switch (methodId)
        {
            case WorldNtfMethodIds.EnterScene:        OnEnterScene(payload);    break;
            case WorldNtfMethodIds.SyncServerTime:    OnServerTime(payload);    break;
            case WorldNtfMethodIds.SyncNearEntities:  OnNearEntities(payload);  break;
            // Delta paths take the byte[] as ReadOnlyMemory so the reader chain can slice
            // attr payloads zero-copy off this per-packet array (AttrCollectionReader note).
            case WorldNtfMethodIds.SyncNearDeltaInfo: OnNearDelta(payload);     break;
            case WorldNtfMethodIds.SyncToMeDeltaInfo: OnSelfDelta(payload);     break;
        }
    }
}
