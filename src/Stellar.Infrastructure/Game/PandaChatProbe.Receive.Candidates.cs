using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Receive-candidate discovery for <see cref="PandaChatProbe"/>: enumerates
/// ZRpcImpl/ZRpcCtrl methods whose shape matches a receive site
/// (<see cref="CollectReceiveCandidates"/> +
/// <see cref="ChooseMessageArgIndex"/>) and produces the per-site
/// <see cref="HookSite"/> descriptors keyed by <see cref="MethodBase"/> for
/// the postfix dispatcher on <c>.Receive.Dispatch.cs</c> to look up at call
/// time.
/// </summary>
internal sealed partial class PandaChatProbe
{
    // Per-method hook descriptor, keyed by MethodBase (same pattern as
    // HarmonyGameMethodHooker.Callbacks). MetadataToken is unreliable for
    // IL2CPP-wrapped methods. Written by PatchAll (Orchestration); read by
    // OnReceiveMethodCalled (Dispatch).
    private static readonly ConcurrentDictionary<MethodBase, HookSite> SiteByMethod = new();

    /// <summary>
    /// Hook site descriptor — captured once during PatchAll, looked up per-call by
    /// the postfix using __originalMethod as the dictionary key.
    /// </summary>
    private sealed class HookSite
    {
        public string Tag = string.Empty;
        public int MessageArgIndex;     // index into __args of the message-or-wrapper parameter
        public bool ArgIsStubCall;      // true => use _stubCall* accessors; false => tolerant extract
        public PropertyInfo? MsgProperty;   // cached wrapper-message accessor (non-StubCall sites)
        public FieldInfo? MsgField;
        public Type? CachedArgType;     // type used to resolve MsgProperty/MsgField; flushed on type drift
    }

    private static MethodInfo? FindAddStubCall(Type rpcImplType)
    {
        foreach (var m in rpcImplType.GetMethods(AnyInstance))
        {
            if (m.Name != "AddStubCall") continue;
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// Scan a type for receive-shaped instance methods: name starts with
    /// Process / Lua / Proxy / Notify, takes at least one parameter, and that
    /// FIRST parameter is IStubCall, IBufferMessage/IMessage, or System.Object.
    /// First-arg-only is the dispatch-prompt rule — it excludes outbound
    /// ProxyCall(IProxy, uint, IBufferMessage, ...) sites whose first arg is
    /// the proxy, not the message.
    /// </summary>
    private static void CollectReceiveCandidates(Type t, List<MethodInfo> sink, MethodInfo? excluded)
    {
        foreach (var m in t.GetMethods(AnyInstance))
        {
            if (m.IsGenericMethodDefinition) continue;
            if (excluded is not null && m.MetadataToken == excluded.MetadataToken && m.DeclaringType == excluded.DeclaringType)
                continue;

            var name = m.Name;
            if (name.Length == 0) continue;
            // Receive-shaped prefixes. `Add*` covers AddStubCall/AddProxyReturn
            // (already-decoded RPC objects being enqueued); `On*` covers
            // OnReturn (response-from-server delivery); `Process*` covers
            // ProcessReturn (chat may flow as a poll response rather than a
            // server push); `Lua/Proxy/Notify` are the additional candidates
            // listed in the Phase 2 dispatch prompt.
            if (!(name.StartsWith("Process", StringComparison.Ordinal)
                  || name.StartsWith("Lua", StringComparison.Ordinal)
                  || name.StartsWith("Proxy", StringComparison.Ordinal)
                  || name.StartsWith("Notify", StringComparison.Ordinal)
                  || name.StartsWith("Add", StringComparison.Ordinal)
                  || name.StartsWith("On", StringComparison.Ordinal)))
            {
                continue;
            }

            var (idx, _) = ChooseMessageArgIndex(m);
            if (idx != 0) continue; // first-arg-only

            sink.Add(m);
        }
    }

    /// <summary>
    /// Inspect the first parameter only. We deliberately accept only CONCRETE
    /// wrapper/message types — patching methods whose first arg is an
    /// interface (e.g. <c>IStubCall</c> on <c>ProcessCall</c>) corrupts the
    /// IL2CPP dispatch and stalls world load. Empirically verified
    /// 2026-05-21 (in-world hung past enter-game with ProcessCall hooked).
    ///
    /// Returns (0, true) when the first arg is a concrete <c>StubCall</c>;
    /// (0, false) when it's another concrete RPC wrapper (<c>ProxyReturn</c>),
    /// or a concrete IBufferMessage/IMessage protobuf payload, or
    /// System.Object. Returns (-1, false) otherwise.
    /// </summary>
    private static (int Index, bool IsStubCall) ChooseMessageArgIndex(MethodInfo m)
    {
        var ps = m.GetParameters();
        if (ps.Length == 0) return (-1, false);

        var pt = ps[0].ParameterType;
        if (pt is null) return (-1, false);

        // Skip interfaces — Harmony+IL2CPP can't safely trampoline interface
        // dispatch on the receive thread. ProcessCall(IStubCall, Nullable<long>)
        // is the canonical example.
        if (pt.IsInterface) return (-1, false);

        var name = pt.Name;
        if (name == "StubCall") return (0, true);
        if (name == "ProxyReturn") return (0, false);

        var full = pt.FullName ?? string.Empty;
        if (full == "Google.Protobuf.IBufferMessage" || full == "Google.Protobuf.IMessage")
            return (0, false);

        if (pt == typeof(object)) return (0, false);

        return (-1, false);
    }

    private static string MakeSiteTag(MethodInfo m)
    {
        // Disambiguate overloads by including the parameter count when there are siblings.
        var declared = m.DeclaringType?.Name ?? "?";
        var name = m.Name;
        var paramCount = m.GetParameters().Length;
        return $"{declared}.{name}/{paramCount}";
    }
}
