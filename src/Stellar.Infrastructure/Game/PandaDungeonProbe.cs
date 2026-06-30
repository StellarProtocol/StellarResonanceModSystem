using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Detects <c>WorldNtf.SyncDungeonData</c> packets and feeds the decoded run id
/// + settlement into the Application <see cref="IDungeonStateSink"/>. The exact
/// WorldNtf method id for SyncDungeonData is not known offline (it lives in the
/// runtime-only <c>Panda.ZRpcGen</c>), so this probe does NOT register by method
/// id. Instead it subscribes as a WorldNtf catch-all observer on the shared
/// <see cref="WorldNtfStubDispatcher"/> and structurally matches each WorldNtf
/// payload via <see cref="DungeonSyncReader"/> — accepting only when the shape
/// is a non-zero <c>scene_uuid</c> (field 1), which no other WorldNtf method
/// carries at field 1 as a varint.
///
/// <para>
/// Runs on the network receive thread; never throws across the IL2CPP boundary
/// (the dispatcher wraps the observer call in a try/catch, and the reader is
/// fully defensive). First-seen diagnostics — including the actual method id the
/// packet arrived on, so the user can later switch to a direct method-id
/// registration — live in <c>PandaDungeonProbe.Diagnostics.cs</c>.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private readonly IDungeonStateSink _sink;
    private readonly IPluginLog _log;

    public PandaDungeonProbe(IDungeonStateSink sink, IPluginLog log)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _log  = log  ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Register for the SyncDungeonData method (id 23, confirmed from the game's
    /// lua WorldNtf dispatcher). Must be called before
    /// <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
        => dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtf);

    /// <summary>
    /// Clear the active run id + settlement. Wired to logout so a stale run id
    /// doesn't linger between sessions. NOT wired to leave-scene — the run id is
    /// deliberately held through the return-to-town transition so the upload
    /// plugin can archive against the dungeon id after the player has left.
    /// </summary>
    public void OnLeaveOrLogout() => _sink.Reset();

    // Observer callback: every WorldNtf-uuid packet, any method id. Structural
    // match → update settlement. Non-dungeon methods short-circuit in the reader.
    //
    // This probe NO LONGER touches the run id. SyncDungeonData (method 23) was
    // assumed to fire only inside a dungeon, but in-game it ALSO fires in town —
    // so a town method-23 would promote the town scene as the run id. The run id
    // is now latched directly (and magnitude-gated to dungeon instances) by
    // PandaCombatStubProbe.OnEnterScene; this probe only reads the settlement.
    //
    // We deliberately do NOT read scene_uuid from this payload: DungeonSyncData's
    // field 1 arrives through the game's dirty-mask container encoding and the
    // bare-varint read returns a shifting value within one run (1771, 579, 1) —
    // unreliable as a stable run key. The settlement sub-message IS a full
    // snapshot, so the clear time / master-mode score read here are correct.
    private void OnWorldNtf(uint methodId, byte[] payload)
    {
        if (!DungeonSyncReader.TryRead(payload, out var result))
            return;

        if (result.HasSettlement)
            _sink.SetSettlement(result.PassTimeSeconds, result.MasterModeScore);

        DiagDungeonSync(methodId, result);
    }
}
