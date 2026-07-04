using System;
using Stellar.Abstractions.Services;
using Stellar.Wire;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Taps the login-connection <c>Social.GetSocialData</c> reply and feeds decoded
/// <see cref="SocialSnapshot"/> records into <see cref="ISocialDataSink"/>
/// (the far-player social-data fallback the entity inspector reads when the
/// proximity broadcast is absent).
///
/// <para>
/// Like <c>GetTeamInfo_Ret</c>, the reply arrives as a <see cref="WireMessageKind.Return"/>
/// on the login connection — Returns carry NO service_uuid/method_id on the wire,
/// so there is no method-id allow-list to register against. Identification is
/// content-based: <see cref="Start"/> registers an <see cref="IWireTap.RegisterReturn"/>
/// handler that attempts to decode every Return as a social reply; a successful
/// decode IS the identification, and unrelated Returns yield null and are ignored.
/// </para>
///
/// <para>
/// Wire shape: the server wraps the reply in a <c>Social.GetSocialData_Ret { GetSocialDataReply ret = 1 }</c>
/// envelope (mirrors <c>GetTeamInfo_Ret</c>). The handler unwraps field 1 to the inner
/// <c>GetSocialDataReply</c> bytes before handing them to <see cref="SocialDataReader.Read"/>,
/// which decodes <c>GetSocialDataReply{ data=2 SocialData }</c> directly — keeping the Wire
/// reader envelope-agnostic (as its tests expect). A defensive direct-parse fallback covers
/// the case where the payload already IS an unwrapped reply.
/// </para>
/// </summary>
internal sealed partial class PandaSocialDataProbe
{
    private readonly ISocialDataSink _sink;
    private readonly IWireTap        _wireTap;
    private readonly IPluginLog      _log;

    public PandaSocialDataProbe(ISocialDataSink sink, IWireTap wireTap, IPluginLog log)
    {
        _sink    = sink    ?? throw new ArgumentNullException(nameof(sink));
        _wireTap = wireTap ?? throw new ArgumentNullException(nameof(wireTap));
        _log     = log     ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Registers the all-Returns handler that decodes <c>Social.GetSocialData</c> replies.
    /// Must run after the wire-tap recv hook is installed (<c>PandaWireTap.PatchAll</c>).
    /// </summary>
    public void Start() => _wireTap.RegisterReturn(OnWireReturn);

    private void OnWireReturn(WireEnvelope env)
    {
        var payload = env.Payload.Span;
        if (payload.Length < 4) return;

        var snapshot = Decode(payload);
        if (snapshot is null) return;

        DiagFirstSocialDecode(snapshot);
        LogAvatarUrlOneShot(snapshot);
        LogCollectPointsOneShot(snapshot);
        _sink.Push(snapshot);
    }

    // Unwrap the GetSocialData_Ret { GetSocialDataReply ret = 1 } envelope to the inner
    // GetSocialDataReply, then decode. Falls back to a direct parse for an already-unwrapped
    // reply (defensive). The unwrap lives here (Infrastructure) so SocialDataReader stays a
    // pure GetSocialDataReply decoder, matching the GetTeamInfoReplyReader split.
    private static SocialSnapshot? Decode(ReadOnlySpan<byte> payload)
    {
        int p = 0;
        if (WireProtocol.TryReadTag(payload, ref p, out var field, out var wire)
            && field == 1 && wire == 2
            && WireProtocol.TryReadLengthDelimited(payload, ref p, out var inner))
        {
            var unwrapped = SocialDataReader.Read(inner);
            if (unwrapped is not null) return unwrapped;
        }

        // Defensive: payload already IS GetSocialDataReply (no outer _Ret envelope).
        return SocialDataReader.Read(payload);
    }
}
