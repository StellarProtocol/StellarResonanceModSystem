using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Orchestration partial for <see cref="PandaChatProbe"/> — drives the boot-time
/// patch install. <see cref="PatchAll"/> resolves the RPC types, publishes the
/// singleton, installs the receive-postfix battery, then layers on the send-side
/// hooks. The reflection-heavy resolution helpers it depends on live in
/// <c>PandaChatProbe.Reflection.cs</c>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    public void PatchAll()
    {
        if (_patched) return;
        _patched = true;

        try { DiagPatchAllStart(); }
        catch (Exception ex) { _log.Warning($"[ChatRecon] sweep threw: {ex.GetType().Name}: {ex.Message}"); }

        if (!ResolveRpcTypes(out var rpcImplType, out var rpcCtrlType)) return;

        ResolveProxyReturnUnwrap();

        // Publish the singleton before patching so postfixes can find us even if
        // the first packet arrives before Patch returns.
        Instance = this;

        InstallReceivePostfixes(rpcImplType, rpcCtrlType);

        ResolveTcpSendHook();

        // Outbound channel capture via ZRpcImpl.ProxyCall. The previous broad
        // attempt (patching ALL ProxyCall overloads) black-screened the game.
        // This retry narrows the patch to a single overload — the byte[] body
        // variant used by the Lua chat path — and wraps install in try/catch
        // so a failure here can't disable the rest of the framework.
        try
        {
            TryHookProxyCallForChannelCapture(rpcImplType);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] ProxyCall hook install threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Collect, de-dupe, and patch receive-shaped methods on ZRpcImpl and
    // ZRpcCtrl. AddStubCall(StubCall) comes first (the verified site); other
    // candidates fill out remaining MaxHooks slots.
    private void InstallReceivePostfixes(Type rpcImplType, Type? rpcCtrlType)
    {
        // Build the candidate list. Order matters — AddStubCall first (the
        // verified-but-not-yet-firing-for-chat site), then the new sites.
        var candidates = new List<MethodInfo>(capacity: 8);

        var addStubCall = FindAddStubCall(rpcImplType);
        if (addStubCall is not null) candidates.Add(addStubCall);

        CollectReceiveCandidates(rpcImplType, candidates, excluded: addStubCall);

        if (rpcCtrlType is not null)
        {
            // ZRpcCtrl receive-shaped methods (the LuaProxyCall sibling lives here).
            CollectReceiveCandidates(rpcCtrlType, candidates, excluded: null);
        }

        // De-dupe + cap at MaxHooks.
        var seen = new HashSet<int>();
        var installed = 0;
        foreach (var method in candidates)
        {
            if (installed >= MaxHooks) break;
            if (!seen.Add(method.MetadataToken)) continue;
            if (TryInstallReceivePostfix(method)) installed++;
        }

        if (installed == 0)
        {
            Instance = null;
            _log.Warning("[ChatProbe] no receive hooks installed");
        }
        else
        {
            _log.Info($"[ChatProbe] installed {installed} receive hook(s)");
        }
    }

    // Patch one receive site. Resolves StubCall accessors on first sight if
    // applicable. Returns true on successful patch install.
    private bool TryInstallReceivePostfix(MethodInfo method)
    {
        var (argIndex, argIsStubCall) = ChooseMessageArgIndex(method);
        if (argIndex < 0) return false;

        // Resolve StubCall accessors once if this is the AddStubCall site.
        if (argIsStubCall && _stubCallMsgProperty is null && _stubCallMsgField is null)
        {
            var stubCallType = method.GetParameters()[argIndex].ParameterType;
            _stubCallMsgProperty = stubCallType.GetProperty("CallMsg", AnyInstance)
                ?? stubCallType.GetProperty("callMsg_", AnyInstance)
                ?? stubCallType.GetProperty("Msg", AnyInstance);
            if (_stubCallMsgProperty is null)
            {
                _stubCallMsgField = stubCallType.GetField("callMsg_", AnyInstance)
                    ?? stubCallType.GetField("CallMsg", AnyInstance);
            }
            _uuidProperty = stubCallType.GetProperty("Uuid", AnyInstance)
                ?? stubCallType.GetProperty("uuid_", AnyInstance);
            _methodIdProperty = stubCallType.GetProperty("MethodId", AnyInstance)
                ?? stubCallType.GetProperty("methodId_", AnyInstance);
        }

        var tag = MakeSiteTag(method);
        var site = new HookSite { Tag = tag, MessageArgIndex = argIndex, ArgIsStubCall = argIsStubCall };
        SiteByMethod[method] = site;

        try
        {
            _harmony.Patch(method, postfix: new HarmonyMethod(PostfixDispatchMethod));
            _log.Info($"[ChatProbe] patched receive dispatch: {method.DeclaringType?.FullName}.{method.Name} (argIdx={argIndex} site={tag})");
            return true;
        }
        catch (Exception ex)
        {
            SiteByMethod.TryRemove(method, out _);
            _log.Warning($"[ChatProbe] failed to patch {method.DeclaringType?.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }
}
