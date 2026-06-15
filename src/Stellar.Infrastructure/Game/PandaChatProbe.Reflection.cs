using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection / assembly-enumeration partial for <see cref="PandaChatProbe"/>.
/// Holds the helpers that resolve RPC types, locate the ProxyReturn unwrap
/// helper, and filter the assembly list down to game/plugin-shipped DLLs.
/// </summary>
internal sealed partial class PandaChatProbe
{
    // Resolve ZRpcImpl + ZRpcCtrl from the loaded assemblies. Returns true if
    // ZRpcImpl was found (the required type); ZRpcCtrl is optional (LuaProxyCall
    // sibling site only — receive hooks still install without it).
    private bool ResolveRpcTypes(out Type rpcImplType, out Type? rpcCtrlType)
    {
        Type? impl = null;
        Type? ctrl = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                impl ??= asm.GetType("ZCode.ZRpc.ZRpcImpl", throwOnError: false);
                ctrl ??= asm.GetType("ZCode.ZRpc.ZRpcCtrl", throwOnError: false);
            }
            catch
            {
                // skip unloadable assembly
            }
            if (impl is not null && ctrl is not null) break;
        }
        if (impl is null)
        {
            _log.Warning("[ChatProbe] ZCode.ZRpc.ZRpcImpl not found; chat receive hooks not installed");
            rpcImplType = null!;
            rpcCtrlType = null;
            return false;
        }
        rpcImplType = impl;
        rpcCtrlType = ctrl;
        return true;
    }

    // Resolve GetRetMsg / GetReturnMsg / a similar unwrap helper for ProxyReturn.
    // We don't know exactly which class hosts it (recon was uncertain), so we
    // scan every loaded game/plugin assembly for a method whose name contains
    // "RetMsg" and whose first parameter is IProxyReturn/ProxyReturn. Logs each
    // candidate so we can see what the binary actually shipped. Bounded by the
    // very small number of "RetMsg"-named methods in ZRpc-shaped types.
    private void ResolveProxyReturnUnwrap()
    {
        try
        {
            int candidatesLogged = 0;
            var chosen = ScanForRetMsgMethod(ref candidatesLogged);

            _getRetMsgMethod = chosen;
            if (_getRetMsgMethod is not null)
            {
                var sig = string.Join(",", Array.ConvertAll(_getRetMsgMethod.GetParameters(), p => p.ParameterType.Name));
                _log.Info($"[ChatProbe] resolved {_getRetMsgMethod.DeclaringType?.FullName}.{_getRetMsgMethod.Name}({sig}) -> {_getRetMsgMethod.ReturnType.Name} static={_getRetMsgMethod.IsStatic}");
                BuildIl2CppCastMethod();
            }
            else
            {
                _log.Warning($"[ChatProbe] no RetMsg helper found ({candidatesLogged} candidates seen); ProxyReturn unwrap disabled");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] GetRetMsg resolution threw: {ex.GetType().Name}: {ex.Message}");
            _getRetMsgMethod = null;
        }
    }

    private MethodInfo? ScanForRetMsgMethod(ref int candidatesLogged)
    {
        MethodInfo? chosen = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            chosen = EnumerateRetMsgCandidates(asm, ref candidatesLogged, chosen);
        }
        return chosen;
    }

    // Per-assembly inner scan extracted from ScanForRetMsgMethod. Walks every
    // type whose namespace looks ZRpc-shaped, logs up to 16 RetMsg-named method
    // candidates, and returns the first method whose first parameter is an
    // IProxyReturn/ProxyReturn. Preserves the outer scan's "keep logging even
    // after a winner is chosen" behaviour by accepting and returning the
    // running choice.
    private MethodInfo? EnumerateRetMsgCandidates(Assembly asm, ref int candidatesLogged, MethodInfo? currentChosen)
    {
        const BindingFlags AllScopes = BindingFlags.Static | BindingFlags.Instance
                                     | BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.DeclaredOnly;

        if (!IsGameOrPluginAssembly(asm)) return currentChosen;

        Type[] types;
        try { types = SafeGetTypes(asm); }
        catch { return currentChosen; }

        foreach (var t in types)
        {
            if (t is null) continue;
            var tNs = t.Namespace ?? string.Empty;
            // Restrict to ZCode.ZRpc namespace family — that's where the helper lives per recon.
            if (!Contains(tNs, "ZRpc") && !Contains(t.FullName ?? string.Empty, "Rpc")) continue;

            MethodInfo[] methods;
            try { methods = t.GetMethods(AllScopes); }
            catch { continue; }

            foreach (var m in methods)
            {
                currentChosen = EvaluateRetMsgMethod(t, m, ref candidatesLogged, currentChosen);
            }
        }
        return currentChosen;
    }

    // Inspect a single candidate method: filter out generic-defs / non-matching
    // names, log the candidate (capped at 16), and upgrade the running choice
    // if its first parameter is an IProxyReturn / ProxyReturn shape.
    private MethodInfo? EvaluateRetMsgMethod(Type t, MethodInfo m, ref int candidatesLogged, MethodInfo? currentChosen)
    {
        if (m.IsGenericMethodDefinition) return currentChosen;
        var n = m.Name;
        // Match GetRetMsg, GetReturnMsg, or anything containing "RetMsg".
        if (n.IndexOf("RetMsg", StringComparison.Ordinal) < 0
            && n.IndexOf("ReturnMsg", StringComparison.Ordinal) < 0)
            return currentChosen;

        var ps = m.GetParameters();
        if (candidatesLogged < 16)
        {
            var psDesc = ps.Length == 0
                ? "()"
                : string.Join(",", Array.ConvertAll(ps, p => p.ParameterType.FullName ?? p.ParameterType.Name));
            _log.Info($"[ChatProbe] RetMsg candidate: {t.FullName}.{n}({psDesc}) static={m.IsStatic} ret={m.ReturnType.Name}");
            candidatesLogged++;
        }

        if (currentChosen is null && ps.Length > 0)
        {
            var firstFull = ps[0].ParameterType.FullName ?? string.Empty;
            var firstName = ps[0].ParameterType.Name;
            if (firstName == "IProxyReturn" || firstName == "ProxyReturn"
                || firstFull.EndsWith("IProxyReturn", StringComparison.Ordinal)
                || firstFull.EndsWith("ProxyReturn", StringComparison.Ordinal))
            {
                currentChosen = m;
            }
        }
        return currentChosen;
    }

    // Build the Il2CppObjectBase.Cast<IProxyReturn> closed method. Required to
    // coerce a ProxyReturn into an IProxyReturn reference for Invoke — plain
    // managed reflection rejects the conversion otherwise.
    private void BuildIl2CppCastMethod()
    {
        try
        {
            var iProxyReturn = _getRetMsgMethod!.GetParameters()[0].ParameterType;
            var castOpen = typeof(Il2CppObjectBase).GetMethod(
                "Cast", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (castOpen is not null)
            {
                _il2cppCastMethod = castOpen.MakeGenericMethod(iProxyReturn);
                _log.Info($"[ChatProbe] resolved Il2CppObjectBase.Cast<{iProxyReturn.Name}>");
            }
            else
            {
                _log.Warning("[ChatProbe] Il2CppObjectBase.Cast<T> not found; unwrap will fail");
            }
        }
        catch (Exception castEx)
        {
            _log.Warning($"[ChatProbe] Cast resolution threw: {castEx.GetType().Name}: {castEx.Message}");
        }
    }

    // Recv-side TCP hook now lives on PandaWireTap (single owner). The chat
    // probe consumes parsed envelopes via RegisterOnWireTap. We still need to
    // resolve + patch ZTcpClient.Send so outbound chat send + outbound call_id
    // correlation work — that lives in ResolveTcpClientSend, which we invoke
    // directly after locating ZTcpConnection so the .Send parameter type is
    // reachable.
    private void ResolveTcpSendHook()
    {
        try
        {
            Type? tcpConnType = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    tcpConnType ??= asm.GetType("ZCode.ZNet.ZTcpConnection", throwOnError: false);
                }
                catch
                {
                    // skip unloadable assembly
                }
                if (tcpConnType is not null) break;
            }
            ResolveTcpClientSendFromConnection(tcpConnType);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] ZTcpClient.Send resolution threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Filter out .NET BCL, COSXML, Il2CppSystem, and Unity assemblies — none of
    /// them can hold game code. Keeps assembly enumeration focused on Panda.*,
    /// Zproto.*, Chat.*, ZCode.*, and other plugin/game-shipped assemblies.
    /// </summary>
    private static bool IsGameOrPluginAssembly(Assembly asm)
    {
        var name = SafeAsmName(asm);
        if (name.Length == 0) return false;

        if (name.StartsWith("System", StringComparison.Ordinal)) return false;
        if (name.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
        if (name.StartsWith("mscorlib", StringComparison.Ordinal)) return false;
        if (name.StartsWith("netstandard", StringComparison.Ordinal)) return false;
        if (name == "WindowsBase") return false;

        if (name.StartsWith("Il2CppSystem", StringComparison.Ordinal)) return false;
        if (name.StartsWith("Il2CppMono", StringComparison.Ordinal)) return false;
        if (name.StartsWith("Il2CppInterop", StringComparison.Ordinal)) return false;
        if (name.StartsWith("UnityEngine", StringComparison.Ordinal)) return false;
        if (name.StartsWith("Unity.", StringComparison.Ordinal)) return false;
        if (name == "Mono.Security") return false;

        if (name == "COSXML") return false;
        if (name.StartsWith("Newtonsoft", StringComparison.Ordinal)) return false;
        if (name.StartsWith("HarmonyLib", StringComparison.Ordinal)) return false;
        if (name == "0Harmony" || name == "MonoMod.RuntimeDetour" || name.StartsWith("MonoMod", StringComparison.Ordinal))
            return false;
        if (name.StartsWith("BepInEx", StringComparison.Ordinal)) return false;
        if (name.StartsWith("Stellar", StringComparison.Ordinal)) return false;

        return true;
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = ex.Types;
            if (loaded is null) return Array.Empty<Type>();
            var keep = new List<Type>(loaded.Length);
            foreach (var t in loaded) if (t is not null) keep.Add(t);
            return keep.ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool Contains(string s, string needle)
        => s.IndexOf(needle, StringComparison.Ordinal) >= 0;

    private static string SafeAsmName(Assembly asm)
    {
        try { return asm.GetName().Name ?? "?"; }
        catch { return "?"; }
    }
}
