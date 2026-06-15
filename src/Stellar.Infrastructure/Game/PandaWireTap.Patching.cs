using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// HarmonyX patch installation for <see cref="PandaWireTap"/>. Resolves the
/// TCP (<c>ZCode.ZNet.ZTcpConnection.OnData</c>) and UDP
/// (<c>ZCode.ZNet.ZUdpConnection.ProcessKcpData</c>) recv entry points
/// reflectively and installs PREFIX patches. Failure to install one transport
/// must never take down the other.
/// </summary>
internal sealed partial class PandaWireTap
{
    private static PandaWireTap? Instance;
    private Harmony? _harmony;
    private bool _patched;

    public void PatchAll(string harmonyId)
    {
        if (_patched) return;
        _patched = true;
        _harmony = new Harmony(harmonyId);
        Instance = this;

        if (!PatchTcpConnection()) return;

        // ------ Send tap (call-id → method, for Return correlation) --------
        // Defensive: a failed send tap must never take down the recv path.
        try { PatchTcpSend(); }
        catch (Exception ex) { _log.Warning($"[WireTap] send-tap step threw (continuing): {ex.GetType().Name}: {ex.Message}"); }

        // ------ UDP/KCP recv hook ------------------------------------------
        // Login / chat / social go over ZTcpConnection (patched above). Scene
        // and combat traffic flows over ZUdpConnection (reliable UDP via KCP).
        // ProcessKcpData is the semantic peer of ZTcpConnection.OnData (raw
        // OnRead would carry KCP segment headers — wrong layer). Defensive:
        // failure to install the UDP patch must NOT take down the working TCP
        // path. Catch + warn + continue.
        try
        {
            PatchUdpConnection();
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] UDP patch step threw (continuing without UDP tap): {ex.GetType().Name}: {ex.Message}");
        }

        // Start capture AFTER all patches are installed (off by default; reads STELLAR_WIRECAP).
        try { StartCaptureIfEnabled(); }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] StartCaptureIfEnabled threw (continuing): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Locate ZTcpConnection.OnData(ReadOnlySpan<byte>, ZTcpClient) and install
    // the PREFIX. Returns true on success; on any resolution / patch failure
    // logs a warning, clears Instance, and returns false so the caller skips
    // the UDP step too (no TCP = no point in UDP for this run).
    private bool PatchTcpConnection()
    {
        var tcpConnType = ResolveTcpConnectionType();
        if (tcpConnType is null)
        {
            _log.Warning("[WireTap] ZTcpConnection not found; wire-layer hook not installed");
            Instance = null;
            return false;
        }

        var onData = ResolveOnDataMethod(tcpConnType);
        if (onData is null)
        {
            Instance = null;
            return false;
        }

        var prefix = typeof(PandaWireTap).GetMethod(
            nameof(OnTcpDataReceivedPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            _log.Warning("[WireTap] OnTcpDataReceivedPrefix method missing (build error)");
            Instance = null;
            return false;
        }

        try
        {
            // Prefix instead of postfix: OnData clears the buffer with 0x40
            // markers before returning, so a postfix sees only the cleared
            // pool memory. A prefix runs BEFORE the original method body, so
            // we observe the populated buffer.
            _harmony!.Patch(onData, prefix: new HarmonyMethod(prefix));
            _log.Info($"[WireTap] patched ZTcpConnection.OnData (PREFIX): {tcpConnType.FullName}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] failed to patch {tcpConnType.FullName}.OnData: {ex.GetType().Name}: {ex.Message}");
            Instance = null;
            return false;
        }
    }

    private static Type? ResolveTcpConnectionType()
    {
        Type? tcpConnType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { tcpConnType ??= asm.GetType("ZCode.ZNet.ZTcpConnection", throwOnError: false); }
            catch { /* skip unloadable assembly */ }
            if (tcpConnType is not null) break;
        }
        return tcpConnType;
    }

    // Walk declared instance methods looking for OnData with two parameters
    // whose FIRST parameter is ReadOnlySpan<T>. The IL2CPP-projected name is
    // "ReadOnlySpan`1".
    private MethodInfo? ResolveOnDataMethod(Type tcpConnType)
    {
        MethodInfo[] methods;
        try
        {
            methods = tcpConnType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] GetMethods({tcpConnType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

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

        _log.Warning("[WireTap] OnData(ReadOnlySpan<byte>, ZTcpClient) not found");
        return null;
    }

    /// <summary>
    /// Locate <c>ZCode.ZNet.ZUdpConnection.ProcessKcpData(ReadOnlySpan&lt;byte&gt;)</c>
    /// and install a HarmonyX PREFIX that funnels post-KCP-reassembly bytes
    /// into the same reassembly + dispatch pipeline as TCP. Mirrors the TCP
    /// patch path.
    ///
    /// <c>ProcessKcpData</c> is the semantic peer of <c>ZTcpConnection.OnData</c>
    /// — it fires AFTER KCP reassembles a complete application-layer payload,
    /// so the bytes it receives are zproto frames (the same shape the wire-frame
    /// parser already understands). The earlier alternative — <c>OnRead</c> —
    /// sees raw KCP datagrams with KCP segment headers (cmd/conv/frg/wnd/ts/sn
    /// /una/len) which would desync our size-prefixed drain loop.
    ///
    /// Connection identity is the <c>ZUdpConnection</c> instance itself
    /// (<c>__instance</c>); the reassembly map is keyed by reference so any
    /// stable object identity works for buffering and downstream dispatch.
    /// </summary>
    private void PatchUdpConnection()
    {
        Type? udpConnType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { udpConnType ??= asm.GetType("ZCode.ZNet.ZUdpConnection", throwOnError: false); }
            catch { /* skip unloadable assembly */ }
            if (udpConnType is not null) break;
        }

        if (udpConnType is null)
        {
            _log.Warning("[WireTap] ZUdpConnection not found; UDP wire-layer hook not installed (TCP still active)");
            return;
        }

        var processKcp = ResolveProcessKcpDataMethod(udpConnType);
        if (processKcp is null) return;

        var prefix = typeof(PandaWireTap).GetMethod(
            nameof(OnUdpDataReceivedPrefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            _log.Warning("[WireTap] OnUdpDataReceivedPrefix method missing (build error)");
            return;
        }

        try
        {
            _harmony!.Patch(processKcp, prefix: new HarmonyMethod(prefix));
            _log.Info($"[WireTap] patched ZUdpConnection.ProcessKcpData (PREFIX): {udpConnType.FullName}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] failed to patch {udpConnType.FullName}.ProcessKcpData: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Walk declared instance methods looking for ProcessKcpData with a single
    // ReadOnlySpan<T> parameter. The IL2CPP-projected name is
    // "ReadOnlySpan`1". Returns null on resolution failure (logs and lets the
    // caller skip UDP cleanly).
    private MethodInfo? ResolveProcessKcpDataMethod(Type udpConnType)
    {
        MethodInfo[] methods;
        try
        {
            methods = udpConnType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[WireTap] GetMethods({udpConnType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }

        foreach (var m in methods)
        {
            if (m.Name != "ProcessKcpData") continue;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 1) continue;
            if (ps[0].ParameterType?.Name != "ReadOnlySpan`1") continue;
            return m;
        }

        _log.Warning("[WireTap] ZUdpConnection.ProcessKcpData(ReadOnlySpan<byte>) not found; UDP wire-layer hook not installed (TCP still active)");
        return null;
    }
}
