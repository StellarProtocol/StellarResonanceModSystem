using System;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game.Protobuf;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Routes <c>GrpcTeamNtf</c> method payloads from the shared
/// <see cref="GrpcTeamNtfStubDispatcher"/> into <see cref="IPartyEventSink"/>
/// and falls back to <see cref="IWireTap"/> registration when the stub class
/// isn't present at bring-up.
///
/// <para>
/// The probe no longer installs its own HarmonyX postfix — call
/// <see cref="RegisterWith"/> to subscribe the six wired GrpcTeamNtf method
/// IDs to the shared dispatcher, then let the dispatcher's
/// <c>Install</c> activate the hook. Mirrors the pattern of
/// <see cref="PandaCombatStubProbe"/> on <see cref="WorldNtfStubDispatcher"/>.
/// </para>
///
/// <para>
/// The full roster on login arrives as GetTeamInfo_Ret — a Return on the login
/// connection (no service_uuid on the wire). <see cref="Start"/> registers a
/// <see cref="IWireTap.RegisterReturn"/> handler for this path regardless of
/// whether the stub or wire-tap path is used. The GetTeamInfo_Ret envelope is
/// NOT wrapped in a v_request field — it is parsed directly by
/// <see cref="GetTeamInfoReplyReader"/>; do not regress this path.
/// </para>
/// </summary>
internal sealed partial class PandaPartyStubProbe
{
    private readonly IPartyEventSink _sink;
    private readonly IWireTap        _wireTap;
    private readonly IPluginLog      _log;

    private bool _wireTapAttached;

    public PandaPartyStubProbe(
        IPartyEventSink sink,
        IWireTap        wireTap,
        IPluginLog      log)
    {
        _sink    = sink    ?? throw new ArgumentNullException(nameof(sink));
        _wireTap = wireTap ?? throw new ArgumentNullException(nameof(wireTap));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Subscribes the six wired GrpcTeamNtf method IDs to the shared dispatcher.
    /// Must be called before <see cref="GrpcTeamNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(GrpcTeamNtfStubDispatcher dispatcher)
    {
        dispatcher.Register(GrpcTeamNtfMethodIds.NotifyJoinTeam,             Dispatch);
        dispatcher.Register(GrpcTeamNtfMethodIds.NotifyLeaveTeam,            Dispatch);
        dispatcher.Register(GrpcTeamNtfMethodIds.NoticeUpdateTeamInfo,       Dispatch);
        dispatcher.Register(GrpcTeamNtfMethodIds.NoticeUpdateTeamMemberInfo, Dispatch);
        dispatcher.Register(GrpcTeamNtfMethodIds.NotifyTeamGroupUpdate,      Dispatch);
        dispatcher.Register(GrpcTeamNtfMethodIds.NoticeTeamDissolve,         Dispatch);
    }

    /// <summary>
    /// Attaches the WireTap fallback (used when <see cref="GrpcTeamNtfStubDispatcher"/>
    /// could not install — i.e. the stub type wasn't found at bring-up) and the
    /// unconditional GetTeamInfo_Ret return handler for the login-roster capture.
    /// Must be called after <see cref="RegisterWith"/> so the Return handler is
    /// always live regardless of stub availability.
    /// </summary>
    public void Start(bool stubAttached)
    {
        if (!stubAttached && !_wireTapAttached)
        {
            _log.Warning("[PartyProbe] GrpcTeamNtfStub not found — falling back to PandaWireTap");
            AttachWireTapFallback();
        }

        // The full roster on login arrives as GetTeamInfo_Ret — a Return on the
        // login connection (no service_uuid on the wire). Notifies (join/leave)
        // only cover incremental changes, so without this a fresh login shows an
        // empty party until members act. Decode every Return as GetTeamInfoReply;
        // a strong content check rejects unrelated replies.
        _wireTap.RegisterReturn(OnWireReturn);
    }

    private void OnWireReturn(WireEnvelope env)
    {
        var payload = env.Payload.Span;
        if (payload.Length < 16) return;
        if (GetTeamInfoReplyReader.TryRead(payload, out var snap))
        {
            DiagReturnCandidate(snap);
            if (IsLikelyTeamInfoReply(snap))
            {
                DiagTeamInfoReply(snap.Roster.Count, snap.PartyId);
                _sink.EnqueueFullSnapshot(snap, authoritative: true);   // full fetch — complete roster, prune OK
                return;
            }
        }

        // CreateTeam_Ret: fires the moment you CREATE a party (the game does NOT send a
        // GetTeamInfoReply until the panel is opened). Its base_info is nested in a TeamInfo
        // wrapper + carries no member names yet, so the GetTeamInfoReply path above rejects it.
        // Establish the party identity here so the 5/20 control shows on create — no panel visit.
        if (CreateTeamReplyReader.TryRead(payload, out var created))
        {
            DiagTeamInfoReply(created.Roster.Count, created.PartyId);
            _sink.EnqueueFullSnapshot(created, authoritative: true);   // create reply — complete (new) roster
        }
    }

    // Reject Returns that merely parse field-by-field. A real GetTeamInfoReply has
    // a non-zero team_id + leader, 1–20 members all with valid char-ids, and at
    // least one carries a social name.
    private static bool IsLikelyTeamInfoReply(PartyWireSnapshot s)
    {
        if (s.PartyId <= 0 || s.LeaderCharId <= 0) return false;
        if (s.Roster.Count < 1 || s.Roster.Count > 20) return false;
        bool anyName = false;
        foreach (var r in s.Roster)
        {
            if (r.CharId <= 0) return false;
            if (r.Social is { Name: { Length: > 0 } }) anyName = true;
        }
        return anyName;
    }

    private void AttachWireTapFallback()
    {
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NotifyJoinTeam,             OnWireNotifyJoinTeam);
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NotifyLeaveTeam,            OnWireNotifyLeaveTeam);
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NoticeUpdateTeamInfo,       OnWireNoticeUpdateTeamInfo);
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NoticeUpdateTeamMemberInfo, OnWireNoticeUpdateTeamMemberInfo);
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NotifyTeamGroupUpdate,      OnWireNotifyTeamGroupUpdate);
        _wireTap.Register(BPSRServiceIds.GrpcTeamNtf, GrpcTeamNtfMethodIds.NoticeTeamDissolve,         OnWireNoticeTeamDissolve);
        _wireTapAttached = true;
    }

    private void OnWireNotifyJoinTeam(WireEnvelope env)             => HandleNotifyJoinTeam(env.Payload.Span);
    private void OnWireNotifyLeaveTeam(WireEnvelope env)            => HandleNotifyLeaveTeam(env.Payload.Span);
    private void OnWireNoticeUpdateTeamInfo(WireEnvelope env)       => HandleNoticeUpdateTeamInfo(env.Payload.Span);
    private void OnWireNoticeUpdateTeamMemberInfo(WireEnvelope env) => HandleNoticeUpdateTeamMemberInfo(env.Payload.Span);
    private void OnWireNotifyTeamGroupUpdate(WireEnvelope env)      => HandleNotifyTeamGroupUpdate(env.Payload.Span);
    private void OnWireNoticeTeamDissolve(WireEnvelope env)         => _sink.EnqueueDissolve();

    /// <summary>
    /// Route a GrpcTeamNtf payload to the matching handler. Called by the
    /// dispatcher after it has confirmed uuid==GrpcTeamNtf and the method ID is
    /// subscribed. Signature matches <c>Action&lt;uint, byte[]&gt;</c>.
    /// </summary>
    private void Dispatch(uint methodId, byte[] payload)
    {
        var span = (ReadOnlySpan<byte>)payload;
        DiagOnFirstGrpcTeamNtfCall(methodId, payload.Length);
        try
        {
            switch (methodId)
            {
                case GrpcTeamNtfMethodIds.NotifyJoinTeam:             HandleNotifyJoinTeam(span);             break;
                case GrpcTeamNtfMethodIds.NoticeUpdateTeamInfo:       HandleNoticeUpdateTeamInfo(span);       break;
                case GrpcTeamNtfMethodIds.NoticeUpdateTeamMemberInfo: HandleNoticeUpdateTeamMemberInfo(span); break;
                case GrpcTeamNtfMethodIds.NotifyLeaveTeam:            HandleNotifyLeaveTeam(span);            break;
                case GrpcTeamNtfMethodIds.NotifyTeamGroupUpdate:      HandleNotifyTeamGroupUpdate(span);      break;
                case GrpcTeamNtfMethodIds.NoticeTeamDissolve:         _sink.EnqueueDissolve();                break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[PartyProbe] dispatch methodId={methodId} threw: {ex.Message}");
        }
    }

    private void HandleNotifyJoinTeam(ReadOnlySpan<byte> payload)
    {
        if (!WireProtocol.TryReadVRequest(payload, out var inner)
            || !NotifyJoinTeamReader.TryRead(inner, out var snap))
        {
            DiagPartyMsg("NotifyJoinTeam", payload.Length, -1);
            return;
        }
        DiagPartyMsg("NotifyJoinTeam", payload.Length, snap.Roster.Count);
        _sink.EnqueueFullSnapshot(snap);
    }

    private void HandleNoticeUpdateTeamInfo(ReadOnlySpan<byte> payload)
    {
        if (!WireProtocol.TryReadVRequest(payload, out var inner)
            || !NoticeUpdateTeamInfoReader.TryRead(inner, out var snap))
        {
            DiagPartyMsg("NoticeUpdateTeamInfo", payload.Length, -1);
            return;
        }
        DiagPartyMsg("NoticeUpdateTeamInfo", payload.Length, snap.Roster.Count);
        _sink.EnqueueFullSnapshot(snap);
    }

    private void HandleNoticeUpdateTeamMemberInfo(ReadOnlySpan<byte> payload)
    {
        if (!WireProtocol.TryReadVRequest(payload, out var inner)
            || !NoticeUpdateTeamMemberInfoReader.TryRead(inner, out var msg))
        {
            DiagPartyMsg("NoticeUpdateTeamMemberInfo", payload.Length, -1);
            return;
        }
        DiagPartyMsg("NoticeUpdateTeamMemberInfo", payload.Length, msg.FastSyncs.Count + msg.SocialSyncs.Count);
        foreach (var f in msg.FastSyncs)
            _sink.EnqueueMemberFastSync(f.CharId, f.Data);
        foreach (var s in msg.SocialSyncs)
        {
            if (s.Roster.Social is { } soc)
                _sink.EnqueueMemberSocialSync(s.CharId, soc);
        }
    }

    private void HandleNotifyTeamGroupUpdate(ReadOnlySpan<byte> payload)
    {
        if (!WireProtocol.TryReadVRequest(payload, out var inner)
            || !Protobuf.NotifyTeamGroupUpdateReader.TryRead(inner, out var groups))
        {
            DiagPartyMsg("NotifyTeamGroupUpdate", payload.Length, -1);
            return;
        }
        DiagPartyMsg("NotifyTeamGroupUpdate", payload.Length, groups.Count);
        _sink.EnqueueGroupLayout(groups);
    }

    private void HandleNotifyLeaveTeam(ReadOnlySpan<byte> payload)
    {
        if (!WireProtocol.TryReadVRequest(payload, out var inner)
            || !NotifyLeaveTeamReader.TryRead(inner, out var msg))
        {
            DiagPartyMsg("NotifyLeaveTeam", payload.Length, -1);
            return;
        }
        DiagPartyMsg("NotifyLeaveTeam", payload.Length, 1);
        _sink.EnqueueMemberLeft(msg.CharId, msg.LeaveTypeRaw);
    }
}
