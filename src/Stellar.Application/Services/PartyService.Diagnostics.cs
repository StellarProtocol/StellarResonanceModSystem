using Stellar.Abstractions.Diagnostics;

namespace Stellar.Application.Services;

/// <summary>
/// Opt-in roster-state dump for <see cref="PartyService"/>. Gated on
/// <c>STELLAR_DIAGNOSTICS=1</c> so steady-state Drain pays zero cost. Logged only
/// when a Drain produced events (i.e. the roster changed) — reveals exactly what
/// the party wire delivered: slot count + each member's profession / group / name,
/// which distinguishes "no roster at all" from "members present but fields unsynced
/// (FastSync only, no SocialSync)".
/// </summary>
internal sealed partial class PartyService
{
    private void DiagRoster()
    {
        if (!StellarDiagnostics.IsEnabled) return;

        _log.Info($"[PartyState.Diag] slots={_slots.Count} partyId={_partyId} leader={_leaderCharId} isLeader={IsLeader} partyType={_partyType} isInParty={IsInParty} localChar={_localCharId}");
        foreach (var s in _slots.Values)
            _log.Info($"[PartyState.Diag]   char={s.CharId} prof={s.Profession} group={s.GroupId} slot={s.Slot} online={s.OnlineStatusRaw} state={s.FastSyncStateRaw} name='{s.Name}' hp={s.Hp}/{s.MaxHp}");
    }

    // Fast-sync state-transition log (A2 calibration, 2026-07-17 sync spec). The game client
    // discards this field, so its enum can only be calibrated by observing transitions live:
    // run one STELLAR_DIAGNOSTICS session with party members dying / reviving / logging off /
    // zoning and transcribe the (from->to, context) pairs into the calibration notes.
    //
    // Takes the DTO (not its unpacked fields) so this stays at 3 parameters — STELLAR0003 fires
    // at error severity on ANY method with >5 parameters (SizeAndShapeAnalyzer.cs, MaxParameters=5)
    // with no diagnostics-partial exemption, so a 6-parameter signature here fails the build.
    private void DiagFastSyncState(long charId, int prevState, PartyMemberFastSync data)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PartyState.Diag] fastSync state char={charId} {prevState}->{data.StateRaw} hp={data.Hp}/{data.MaxHp} scene={data.SceneId}");
    }
}
