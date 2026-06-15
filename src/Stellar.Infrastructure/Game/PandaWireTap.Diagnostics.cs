using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaWireTap"/>. Gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state dispatch path pays zero cost.
/// </summary>
internal sealed partial class PandaWireTap
{
    // ---- Send-tap diagnostics -----------------------------------------------
    private bool _diagFirstOutgoingCallLogged;

    /// <summary>Log the first outgoing Call — confirms the send tap fires.</summary>
    private void DiagOutgoingCall(ulong serviceUuid, uint methodId, uint callId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_diagFirstOutgoingCallLogged) return;
        _diagFirstOutgoingCallLogged = true;
        _log.Info($"[WireTap.Send] first outgoing Call: svc={serviceUuid} method={methodId} callId={callId}");
    }

    /// <summary>One-time recon: list every Send/SendMsg overload (incl. inherited)
    /// on the resolved ZTcpClient type with its parameter types, so we can see
    /// which send path the game actually uses if the patched ones don't fire.</summary>
    private void DiagSendSurface(System.Type tcpClientType)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            foreach (var m in tcpClientType.GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                if (m.Name != "Send" && m.Name != "SendMsg" && m.Name != "Write") continue;
                var ps = m.GetParameters();
                var sig = string.Join(", ", System.Array.ConvertAll(ps, p => p.ParameterType?.Name ?? "?"));
                _log.Info($"[WireTap.Send.Surface] {m.DeclaringType?.Name}.{m.Name}({sig}) abstract={m.IsAbstract}");
            }
        }
        catch (System.Exception ex)
        {
            _log.Warning($"[WireTap.Send.Surface] dump threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
