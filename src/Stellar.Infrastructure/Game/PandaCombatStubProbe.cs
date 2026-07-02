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
    private readonly ICombatEventSink  _sink;
    private readonly IDungeonStateSink _dungeonSink;
    private readonly IPluginLog        _log;

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

    public PandaCombatStubProbe(ICombatEventSink sink, IDungeonStateSink dungeonSink, IPluginLog log)
    {
        _sink        = sink        ?? throw new ArgumentNullException(nameof(sink));
        _dungeonSink = dungeonSink ?? throw new ArgumentNullException(nameof(dungeonSink));
        _log         = log         ?? throw new ArgumentNullException(nameof(log));
    }

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
        var span = (ReadOnlySpan<byte>)payload;
        switch (methodId)
        {
            case WorldNtfMethodIds.EnterScene:        OnEnterScene(span);    break;
            case WorldNtfMethodIds.SyncServerTime:    OnServerTime(span);    break;
            case WorldNtfMethodIds.SyncNearEntities:  OnNearEntities(span);  break;
            case WorldNtfMethodIds.SyncNearDeltaInfo: OnNearDelta(span);     break;
            case WorldNtfMethodIds.SyncToMeDeltaInfo: OnSelfDelta(span);     break;
        }
    }
}
