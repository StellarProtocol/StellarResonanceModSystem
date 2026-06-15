using System;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// HarmonyX postfix dispatcher for <see cref="PandaChatProbe"/>. Runs on the
/// network receive thread after each patched ZRpcImpl/ZRpcCtrl receive method
/// returns. Looks up the per-site <see cref="HookSite"/> on
/// <c>.Receive.Candidates.cs</c>, extracts the payload via
/// <c>.Receive.Extraction.cs</c>, recovers the concrete IL2CPP type via
/// <c>.Receive.Resolution.cs</c>, then projects chat-shaped payloads through
/// <c>.Receive.Chat.cs</c>. MUST stay allocation-free on the rejection
/// (non-chat) path.
/// </summary>
internal sealed partial class PandaChatProbe
{
    // HarmonyX postfix — runs on the network receive thread after the patched
    // method returns. MUST stay allocation-free and side-effect-free on the
    // rejection (non-chat) path. The dispatch helper looks up the per-site
    // HookSite via the originating method.
    //
    // We rely on __originalMethod (HarmonyX-supplied) so we don't need a unique
    // postfix per site. Same pattern as HarmonyGameMethodHooker.Trampoline.
    private static void OnReceiveMethodCalled(object?[] __args, MethodBase __originalMethod)
    {
        var probe = Instance;
        if (probe is null) return;
        if (__args is null || __args.Length == 0) return;
        if (__originalMethod is null) return;

        if (!SiteByMethod.TryGetValue(__originalMethod, out var site)) return;

        var argIndex = site.MessageArgIndex;
        if ((uint)argIndex >= (uint)__args.Length) return;

        var firstArg = __args[argIndex];
        if (firstArg is null) return;

        try
        {
            object? msg = site.ArgIsStubCall
                ? probe.ExtractMessageFromStubCall(firstArg)
                : ExtractMessageTolerant(firstArg, site);
            if (msg is null) return;

            var (managedType, concreteType) = RecoverIl2CppMessageType(msg);
            LogDistinctReturnInnerType(probe, site, concreteType);

            if (!IsChatReceiveType(concreteType))
            {
                if (probe._unmatchedPerSite.TryAdd(site.Tag, true))
                {
                    probe._log.Info($"[ChatProbe] {site.Tag} first unmatched: managed='{managedType}' concrete='{concreteType}'");
                }
                return;
            }

            probe._log.Info($"[Chat] received via {site.Tag}: type={concreteType}");
            probe.OnChatReceiveMessage(firstArg, msg, concreteType);
        }
        catch (Exception ex)
        {
            // Postfix runs on the network thread. Never throw across the IL2CPP boundary.
            var probeLog = Instance?._log;
            probeLog?.Warning($"[ChatProbe] {site.Tag} postfix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Two-stage type recovery for a boxed receive payload. Managed reflection
    /// first (cheap). When managed gives us a useless interface name
    /// (<c>Google.Protobuf.IBufferMessage</c>, anything else under
    /// <c>Google.Protobuf.*</c>, or an <c>Il2Cpp*</c> bridge wrapper) fall
    /// back to <see cref="ResolveIl2CppConcreteType"/> which reads the IL2CPP
    /// class namespace/name via the native API. Returns the managed type as
    /// <c>concreteType</c> when no fallback is needed.
    /// </summary>
    private static (string ManagedType, string ConcreteType) RecoverIl2CppMessageType(object msg)
    {
        var managedType = msg.GetType().FullName ?? string.Empty;
        string concreteType = managedType;
        if (managedType.Length == 0
            || managedType == "Google.Protobuf.IBufferMessage"
            || managedType.StartsWith("Google.Protobuf.", StringComparison.Ordinal)
            || managedType.StartsWith("Il2Cpp", StringComparison.Ordinal))
        {
            concreteType = ResolveIl2CppConcreteType(msg) ?? managedType;
        }
        return (managedType, concreteType);
    }

    /// <summary>
    /// Per-distinct-inner-type one-shot diagnostic — scoped to
    /// <c>ProcessReturn</c> / <c>AddProxyReturn</c> sites only (their tag
    /// contains "Return"). AddStubCall has its own first-shot diagnostic at
    /// the chat-projection layer. Bounded by the number of distinct reply
    /// protobuf types observed in a session (~50-200 in the wild).
    /// </summary>
    private static void LogDistinctReturnInnerType(PandaChatProbe probe, HookSite site, string concreteType)
    {
        if (site.Tag.IndexOf("Return", StringComparison.Ordinal) < 0) return;
        if (string.IsNullOrEmpty(concreteType)) return;
        if (!probe._unmatchedInnerTypes.TryAdd(concreteType, true)) return;
        probe._log.Info($"[ChatProbe] {site.Tag} inner type seen: {concreteType}");
    }
}
