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
            _log.Info($"[PartyState.Diag]   char={s.CharId} prof={s.Profession} group={s.GroupId} slot={s.Slot} online={s.OnlineStatusRaw} name='{s.Name}' hp={s.Hp}/{s.MaxHp}");
    }
}
