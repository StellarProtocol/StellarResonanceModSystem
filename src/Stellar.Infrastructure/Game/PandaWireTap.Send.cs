using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Send-side tap for <see cref="PandaWireTap"/>. Patches
/// <c>ZTcpConnection.Send(ReadOnlySpan&lt;byte&gt;)</c> (the symmetric peer of
/// the recv-side <c>OnData</c>), parses outgoing Call headers, and feeds them
/// into <see cref="Capture.WirePacketCapture"/> for correlation. Runs on the
/// network I/O thread; bounded work, never throws across the IL2CPP boundary.
/// </summary>
internal sealed partial class PandaWireTap
{
    private bool _sendPatched;

    // Resolve + patch ZTcpClient.Send(ReadOnlySpan<byte>). The send method lives on
    // ZTcpClient, not ZTcpConnection — we obtain the ZTcpClient type from OnData's
    // 2nd parameter (same anchor PandaChatProbe uses). Defensive: a failed send tap
    // must never take down the recv path.
    private void PatchTcpSend()
    {
        if (_sendPatched) return;
        var tcpConnType = ResolveTcpConnectionType();
        if (tcpConnType is null) { _log.Warning("[WireTap] ZTcpConnection not found; send tap not installed"); return; }

        var onData = ResolveOnDataMethod(tcpConnType);
        Type? tcpClientType = null;
        try
        {
            var ps = onData?.GetParameters();
            if (ps is { Length: >= 2 }) tcpClientType = ps[1].ParameterType;
        }
        catch { /* fall through to the null check */ }
        if (tcpClientType is null) { _log.Warning("[WireTap] ZTcpClient type not resolved from OnData; send tap not installed"); return; }

        var prefix = typeof(PandaWireTap).GetMethod(
            nameof(OnTcpDataSentPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null) { _log.Warning("[WireTap] OnTcpDataSentPrefix missing (build error)"); return; }

        DiagSendSurface(tcpClientType);   // one-time recon: dump available Send/SendMsg overloads

        // The game's outgoing path doesn't use the 1-arg Send(ReadOnlySpan<byte>)
        // (that's a plugin/helper entry — it never fired for game traffic). Patch
        // EVERY non-abstract Send overload whose first parameter is
        // ReadOnlySpan<byte> (covers the 2-arg Send(ReadOnlySpan,object):int the
        // game actually calls). The prefix only reads __args[0] (the span).
        int patched = 0;
        foreach (var send in ResolveSendMethods(tcpClientType))
        {
            try
            {
                _harmony!.Patch(send, prefix: new HarmonyMethod(prefix));
                patched++;
                _log.Info($"[WireTap] patched {tcpClientType.FullName}.Send({send.GetParameters().Length} args) (PREFIX, send tap)");
            }
            catch (Exception ex)
            {
                _log.Warning($"[WireTap] failed to patch Send({send.GetParameters().Length} args): {ex.GetType().Name}: {ex.Message}");
            }
        }
        _sendPatched = patched > 0;
        if (patched == 0) _log.Warning($"[WireTap] no ReadOnlySpan-first Send overload found on {tcpClientType.FullName}; send tap not installed");
    }

    // All non-abstract Send overloads on ZTcpClient whose FIRST parameter is
    // ReadOnlySpan<byte> (IL2CPP-projected "ReadOnlySpan`1"). The real game send
    // is the 2-arg Send(ReadOnlySpan<byte>, object):int; the 1-arg void is a helper.
    private System.Collections.Generic.List<MethodInfo> ResolveSendMethods(Type tcpClientType)
    {
        var result = new System.Collections.Generic.List<MethodInfo>();
        try
        {
            // No DeclaredOnly: the 2-arg Send(ReadOnlySpan,object):int is inherited
            // from a base/interface, not declared on ZTcpClient itself.
            foreach (var m in tcpClientType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "Send" || m.IsAbstract) continue;
                var ps = m.GetParameters();
                if (ps.Length is < 1 or > 2) continue;
                if (ps[0].ParameterType?.Name != "ReadOnlySpan`1") continue;
                result.Add(m);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] resolve Send overloads threw: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    private static void OnTcpDataSentPrefix(object?[] __args)
    {
        var tap = Instance;
        if (tap is null) return;
        try
        {
            if (__args is null || __args.Length < 1 || __args[0] is null) return;
            var bytes = tap.CoerceReceivedBytes(__args[0]!, hookName: "Send");
            if (bytes is null || bytes.Length < 6) return;
            tap.HandleOutgoingFrames(bytes);
        }
        catch (Exception ex)
        {
            Instance?._log.Warning($"[WireTap] Send prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Walk the (possibly batched) size-prefixed outgoing frames; remember the
    // call-id of any GetTeamInfo Call. Same framing as recv: [size:4][flags:2]…
    private void HandleOutgoingFrames(byte[] data)
    {
        ReadOnlySpan<byte> span = data;
        int pos = 0;
        while (pos + 6 <= span.Length)
        {
            uint size = ((uint)span[pos] << 24) | ((uint)span[pos + 1] << 16)
                      | ((uint)span[pos + 2] << 8) | span[pos + 3];
            if (size < 6 || pos + (long)size > span.Length) break;

            var frame = span.Slice(pos, (int)size);
            if (_capture is not null) _capture.Record("out", frame.ToArray(), _currentTcpClient);
            ushort flags = (ushort)((frame[4] << 8) | frame[5]);
            if ((flags & 0x7FFF) == MsgTypeCall
                && TryParseWireHeader(frame, MsgTypeCall, out var h, out _))
            {
                OnOutgoingCall(h.ServiceUuid, h.MethodId, h.CallId);
            }
            pos += (int)size;
        }
    }

    private void OnOutgoingCall(ulong serviceUuid, uint methodId, uint callId)
    {
        DiagOutgoingCall(serviceUuid, methodId, callId);
    }
}
