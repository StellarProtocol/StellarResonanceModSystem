using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Locate <c>ZTcpConnection.OnData(ReadOnlySpan&lt;byte&gt;, ZTcpClient)</c>
    /// purely to read out its second parameter type (ZTcpClient), then resolve
    /// <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> as the dispatch target that
    /// <see cref="TrySend"/> reflection-invokes to inject a plugin chat send, and
    /// install the connection-close hooks. The recv-side wire hook lives on
    /// <see cref="PandaWireTap"/>; we only need OnData here as a reflection anchor
    /// for the ZTcpClient type.
    ///
    /// ⚠️ Outbound call_id / history-channel CAPTURE does NOT happen here. The
    /// game's own RPC never calls this 1-arg <c>ZTcpClient.Send</c> overload — it
    /// is a plugin/helper entry (confirmed: the 2.25.1 prefix on it fired 0 times
    /// for game traffic; <see cref="PandaWireTap"/>'s send tap documents the same),
    /// and chat may route over UDP/KCP entirely. Capture is done at the
    /// transport-agnostic <c>ZRpcImpl.SendBytes</c> instead — see
    /// <c>PandaChatProbe.Send.Hooks.Rpc.cs</c>.
    /// </summary>
    private void ResolveTcpClientSendFromConnection(Type? tcpConnType)
    {
        if (tcpConnType is null)
        {
            _log.Warning("[ChatProbe] ZTcpConnection not found; ZTcpClient.Send not resolved");
            return;
        }

        MethodInfo[] methods;
        try
        {
            methods = tcpConnType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] GetMethods({tcpConnType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var onData = FindOnDataOverload(methods);
        if (onData is null)
        {
            _log.Warning("[ChatProbe] OnData(ReadOnlySpan<byte>, ZTcpClient) not found; ZTcpClient.Send not resolved");
            return;
        }

        try
        {
            var onDataParams = onData.GetParameters();
            if (onDataParams.Length >= 2)
            {
                var tcpClientType = onDataParams[1].ParameterType;
                ResolveTcpClientSend(tcpClientType);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] ZTcpClient.Send resolution threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Pick the <c>OnData(ReadOnlySpan&lt;byte&gt;, ZTcpClient)</c> overload — reflection anchor for the ZTcpClient type. Mirrors <see cref="MatchSendOverload"/>.</summary>
    private static MethodInfo? FindOnDataOverload(MethodInfo[] methods)
    {
        foreach (var m in methods)
        {
            if (m.Name != "OnData") continue;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 2) continue;
            if (ps[0].ParameterType?.Name != "ReadOnlySpan`1") continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// Locate <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> on the supplied type
    /// and store it in <see cref="_tcpClientSend"/> — the overload
    /// <see cref="SendThroughClient"/> reflection-invokes to inject a plugin chat
    /// send. Also installs the connection-close hooks. Does NOT install a Send
    /// PREFIX: the game's RPC dispatch does not use this overload (see the note on
    /// <see cref="ResolveTcpClientSendFromConnection"/>) — outbound capture lives
    /// on <c>ZRpcImpl.SendBytes</c> (<c>PandaChatProbe.Send.Hooks.Rpc.cs</c>). Logs
    /// the chosen overload's parameter type so the log doubles as a recon record.
    /// </summary>
    private void ResolveTcpClientSend(Type tcpClientType)
    {
        MethodInfo[] methods;
        try
        {
            methods = tcpClientType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] GetMethods({tcpClientType.FullName}) for Send threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var best = MatchSendOverload(methods);
        if (best is null)
        {
            _log.Warning($"[ChatProbe] no single-arg Send method found on {tcpClientType.FullName}");
            return;
        }

        _tcpClientSend = best;
        var paramTypeName = best.GetParameters()[0].ParameterType.FullName ?? best.GetParameters()[0].ParameterType.Name;
        _log.Info($"[ChatProbe] resolved {tcpClientType.FullName}.Send({paramTypeName}) as plugin send target");

        InstallConnectionCloseHooks(tcpClientType);
    }

    /// <summary>
    /// Walk <paramref name="methods"/> and pick the best single-arg
    /// <c>Send</c> overload. Preferred: the <c>ReadOnlySpan&lt;byte&gt;</c>
    /// (IL2CPP-projected <c>ReadOnlySpan`1</c>) shape used by BPSR's outbound
    /// dispatch. Falls back to any other single-arg <c>Send</c> so the log at
    /// least surfaces what's available.
    /// </summary>
    private static MethodInfo? MatchSendOverload(MethodInfo[] methods)
    {
        MethodInfo? best = null;
        foreach (var m in methods)
        {
            if (m.Name != "Send") continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;

            // Prefer the ReadOnlySpan<byte> overload. The IL2CPP-projected
            // shape names it "ReadOnlySpan`1" — match by name to be tolerant
            // of either the managed type or the IL2CPP wrapper type.
            var pt = ps[0].ParameterType;
            if (pt?.Name == "ReadOnlySpan`1")
            {
                best = m;
                break;
            }
            // Hold a non-Span single-arg Send as fallback; we'd still need to
            // figure out how to drive it, but at least the log surfaces it.
            best ??= m;
        }
        return best;
    }
}
