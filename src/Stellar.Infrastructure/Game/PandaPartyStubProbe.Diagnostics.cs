using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaPartyStubProbe"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state dispatch path pays zero
/// cost; flip it on to surface the first GrpcTeamNtf stub call and every
/// parsed party message during bring-up.
/// </summary>
internal sealed partial class PandaPartyStubProbe
{
    private bool _diagFirstGrpcTeamNtfCallLogged;

    /// <summary>
    /// One-shot confirmation that the first GrpcTeamNtf stub call landed —
    /// surfaces methodId + payload size so post-patch wiring can be verified.
    /// </summary>
    private void DiagOnFirstGrpcTeamNtfCall(uint methodId, int payloadLen)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_diagFirstGrpcTeamNtfCallLogged) return;

        _log.Info($"[PartyProbe.Diag] first GrpcTeamNtf call: methodId={methodId} payload={payloadLen}B");
        _diagFirstGrpcTeamNtfCallLogged = true;
    }

    /// <summary>
    /// Logs each roster-bearing party message as it is handled (stub or wiretap
    /// path): the message name, payload size, and parsed member/roster count
    /// (<paramref name="members"/> = -1 ⇒ the protobuf reader rejected it).
    /// </summary>
    private void DiagPartyMsg(string method, int payloadLen, int members)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PartyProbe.Diag] {method} payload={payloadLen}B members={members}");
    }

    /// <summary>Logs when a Return was accepted as a GetTeamInfoReply and fed to the
    /// roster — the proof the login-roster capture works.</summary>
    private void DiagTeamInfoReply(int members, long partyId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PartyProbe.Diag] GetTeamInfoReply applied: members={members} partyId={partyId}");
    }

    private int _diagReturnCandidates;

    /// <summary>Logs the first few Returns that decode as a plausible GetTeamInfoReply
    /// (before validation) — reveals whether the reply arrives + decodes and why the
    /// content check might reject it (e.g. empty names, zero partyId).</summary>
    private void DiagReturnCandidate(PartyWireSnapshot s)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (s.PartyId == 0 && s.Roster.Count == 0) return;   // not even a candidate
        if (_diagReturnCandidates >= 5) return;
        _diagReturnCandidates++;
        string firstName = s.Roster.Count > 0 ? (s.Roster[0].Social?.Name ?? "<null>") : "<none>";
        _log.Info($"[PartyProbe.Diag] Return candidate: partyId={s.PartyId} leader={s.LeaderCharId} members={s.Roster.Count} firstName='{firstName}'");
    }
}
