using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection + UniTask plumbing for <see cref="PlayerProfileService"/>: acquiring the Social proxy,
/// building the request, invoking <c>GetSocialData</c>, polling the returned <c>UniTask</c>, and reading
/// the avatar URL off the reply. All resolved lazily and cached; every step fails soft (null, never a throw
/// back into game code). See the main partial for the design overview.
/// </summary>
internal sealed partial class PlayerProfileService
{
    // One-shot resolution latches (types/methods are stable once the hot-update is loaded).
    private bool _metaResolved;
    private bool _metaOk;
    private bool _proxyMembersResolved;
    private bool _proxyMembersOk;
    private bool _utResolved;
    private bool _firstRequestLogged;

    private Type? _rpcCtrlType;                 // ZCode.ZRpc.ZRpcCtrl (ZSingleton)
    private Type? _requestType;                 // GetSocialDataRequest
    private MethodInfo? _getSocialProxy;        // static Zservice.ServiceRegister.GetSocialProxy(IRpcCtrl, bool)
    private MethodInfo? _getSocialData;         // SocialProxy.GetSocialData(request, uint) -> UniTask<GetSocialDataReply>
    private PropertyInfo? _charIdProp;          // GetSocialDataRequest.CharId (int64)
    private PropertyInfo? _maskProp;            // GetSocialDataRequest.Mask   (int64)

    // UniTask<GetSocialDataReply> ops (single closed generic here).
    private MethodInfo? _utStatus;
    private MethodInfo? _utGetAwaiter;
    private MethodInfo? _utGetResult;

    // GetSocialDataReply.Data.AvatarInfo.Profile.Url chain.
    private PropertyInfo? _errCodeProp;
    private PropertyInfo? _dataProp;
    private PropertyInfo? _avatarInfoProp;
    private PropertyInfo? _profileProp;
    private PropertyInfo? _urlProp;

    // Warnings are deduped by message so a persistent structural miss can't spam the log.
    private readonly HashSet<string> _warned = new();

    // Main-thread. Resolve the Social proxy fresh, build the request, invoke GetSocialData. Returns the boxed
    // UniTask<GetSocialDataReply>, or null on any failure (pre-login, structural miss, or an invoke throw).
    private object? TryStartRequest(long charId)
    {
        if (!EnsureMetadata()) return null;

        object? proxy = TryResolveProxy();
        if (proxy is null) return null;

        if (!EnsureProxyMembers(proxy)) return null;

        object? request = BuildRequest(charId);
        if (request is null) return null;

        try
        {
            // GetSocialData(GetSocialDataRequest, uint cancelToken) -> UniTask<GetSocialDataReply>.
            // cancelToken 0 = no cancellation (the game's ZCancelSource.NeverCancelToken is 0).
            var uniTask = _getSocialData!.Invoke(proxy, new object[] { request, (uint)0 });
            if (!_firstRequestLogged)
            {
                _firstRequestLogged = true;
                _log.Info($"[PlayerProfile] first GetSocialData issued (charId={charId}, mask={AvatarMask})");
            }
            return uniTask;
        }
        catch (Exception ex)
        {
            WarnOnce($"GetSocialData invoke threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // One-shot resolution of the static plumbing: the ServiceRegister.GetSocialProxy extension + the ZRpcCtrl
    // singleton type. Does NOT latch while the types are still absent (hot-update not loaded) so a later request
    // retries; latches off only on a genuine structural miss once the types are present.
    private bool EnsureMetadata()
    {
        if (_metaResolved) return _metaOk;

        _rpcCtrlType = StellarInterop.FindType("ZCode.ZRpc.ZRpcCtrl");
        var serviceRegister = StellarInterop.FindType("Zservice.ServiceRegister");
        if (_rpcCtrlType is null || serviceRegister is null)
            return false;   // hot-update not loaded yet — retry on the next request

        _metaResolved = true;
        _getSocialProxy = FindGetSocialProxy(serviceRegister);
        _metaOk = _getSocialProxy is not null;
        if (!_metaOk)
            _log.Warning("[PlayerProfile] Zservice.ServiceRegister.GetSocialProxy(IRpcCtrl, bool) not found — avatar lookups disabled");
        return _metaOk;
    }

    // Resolve the Social proxy fresh every request (never cached): ZRpcCtrl.Instance is a ZSingleton, and the
    // game's own GetSocialProxy returns the registered proxy for the CURRENT transport — so there is no stale
    // handle to go bad across a reconnect (mirrors the chat probe's resolve-live-on-demand doctrine). Null
    // pre-login (singleton not up) or on a throw.
    private object? TryResolveProxy()
    {
        try
        {
            var ctrl = StellarInterop.GetSingleton(_rpcCtrlType!);   // ZRpcCtrl.Instance (FlattenHierarchy base walk)
            if (ctrl is null) return null;
            return _getSocialProxy!.Invoke(null, new object[] { ctrl, true });   // attached:true, as the game calls it
        }
        catch (Exception ex)
        {
            WarnOnce($"GetSocialProxy resolve threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // One-shot resolution of the per-proxy members off the concrete SocialProxy type (stable once resolved):
    // GetSocialData(request, uint), its request type, and the CharId/Mask setters on that request.
    private bool EnsureProxyMembers(object proxy)
    {
        if (_proxyMembersResolved) return _proxyMembersOk;
        _proxyMembersResolved = true;

        _getSocialData = FindGetSocialData(proxy.GetType());
        if (_getSocialData is null)
        {
            _log.Warning("[PlayerProfile] SocialProxy.GetSocialData(request, uint) not found — avatar lookups disabled");
            return false;
        }

        _requestType = _getSocialData.GetParameters()[0].ParameterType;   // GetSocialDataRequest
        _charIdProp = StellarInterop.FindPropertyUp(_requestType, "CharId");
        _maskProp = StellarInterop.FindPropertyUp(_requestType, "Mask");
        _proxyMembersOk = _charIdProp is not null && _maskProp is not null;
        if (!_proxyMembersOk)
            _log.Warning($"[PlayerProfile] GetSocialDataRequest CharId/Mask not found (charId={_charIdProp is not null}, mask={_maskProp is not null})");
        return _proxyMembersOk;
    }

    // Instantiate GetSocialDataRequest via its parameterless IL2CPP ctor (as LightModelCalls does for game
    // objects) and set CharId + Mask (both int64). Returns null on a ctor/set failure.
    private object? BuildRequest(long charId)
    {
        try
        {
            var request = Activator.CreateInstance(_requestType!);
            if (request is null) return null;
            _charIdProp!.SetValue(request, charId);
            _maskProp!.SetValue(request, AvatarMask);   // bit 2 = SocialDataTypeAvatar (the avatar block)
            return request;
        }
        catch (Exception ex)
        {
            WarnOnce($"GetSocialDataRequest build threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // Poll one boxed UniTask<GetSocialDataReply>. Returns true once the task has SETTLED (success/fault/cancel,
    // or it cannot be polled) — in which case `url` is the avatar URL on success, else null — and false while it
    // is still pending. Never throws (a throw is reported as settled-with-null, fail-safe).
    private bool TryPoll(object uniTask, out string? url)
    {
        url = null;
        try
        {
            var (statusGet, getAwaiter, getResult) = UniTaskOps(uniTask);
            if (statusGet is null || getAwaiter is null || getResult is null)
            {
                WarnOnce("UniTask Status/GetAwaiter/GetResult not resolved");
                return true;   // can't poll → settle with null
            }

            var statusObj = statusGet.Invoke(uniTask, null);
            int status = statusObj is null ? 0 : Convert.ToInt32(statusObj);   // 0=Pending 1=Succeeded 2=Faulted 3=Canceled
            if (status == 0) return false;   // still pending
            if (status != 1) return true;    // faulted / canceled → null

            // Succeeded: consume the awaiter's result exactly once and read the URL off the reply.
            var awaiter = getAwaiter.Invoke(uniTask, null);
            var reply = awaiter is null ? null : getResult.Invoke(awaiter, null);
            url = ExtractAvatarUrl(reply);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce($"UniTask poll threw: {ex.GetType().Name}: {ex.Message}");
            return true;   // never rethrow across the boundary
        }
    }

    // Resolve + cache Status/GetAwaiter/Awaiter.GetResult off the concrete UniTask<GetSocialDataReply> type
    // (there is only one such closed generic here). Mirrors GameAssetsService.UniTaskOps.
    private (MethodInfo? Status, MethodInfo? Awaiter, MethodInfo? GetResult) UniTaskOps(object uniTask)
    {
        if (_utResolved) return (_utStatus, _utGetAwaiter, _utGetResult);
        _utResolved = true;
        const BindingFlags f = BindingFlags.Instance | BindingFlags.Public;
        var t = uniTask.GetType();
        _utStatus = t.GetProperty("Status", f)?.GetGetMethod();
        _utGetAwaiter = t.GetMethod("GetAwaiter", f, binder: null, types: Type.EmptyTypes, modifiers: null);
        _utGetResult = _utGetAwaiter?.ReturnType.GetMethod("GetResult", f, binder: null, types: Type.EmptyTypes, modifiers: null);
        return (_utStatus, _utGetAwaiter, _utGetResult);
    }

    // reply.Data.AvatarInfo.Profile.Url — each link resolved lazily off the live object's type (stable across
    // replies) and cached. Null when the reply carries ErrCode != 0, any link is null, or the URL is null/empty.
    private string? ExtractAvatarUrl(object? reply)
    {
        if (reply is null) return null;
        try
        {
            _errCodeProp ??= StellarInterop.FindPropertyUp(reply.GetType(), "ErrCode");
            if (_errCodeProp is not null)
            {
                var ec = _errCodeProp.GetValue(reply);
                if (ec is not null && Convert.ToInt64(ec) != 0) return null;   // server-side failure
            }

            _dataProp ??= StellarInterop.FindPropertyUp(reply.GetType(), "Data");
            var data = _dataProp?.GetValue(reply);
            if (data is null) return null;

            _avatarInfoProp ??= StellarInterop.FindPropertyUp(data.GetType(), "AvatarInfo");
            var avatar = _avatarInfoProp?.GetValue(data);
            if (avatar is null) return null;

            _profileProp ??= StellarInterop.FindPropertyUp(avatar.GetType(), "Profile");
            var profile = _profileProp?.GetValue(avatar);
            if (profile is null) return null;

            _urlProp ??= StellarInterop.FindPropertyUp(profile.GetType(), "Url");
            var url = _urlProp?.GetValue(profile) as string;
            return string.IsNullOrEmpty(url) ? null : url;
        }
        catch (Exception ex)
        {
            WarnOnce($"reply avatar-url read threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // (name, bool) static extension — GetSocialProxy(IRpcCtrl ctrl, bool attached). Matched by param type
    // (bool second arg) rather than arity alone, per the StellarInterop FindMethod keep-vs-swap rule.
    private static MethodInfo? FindGetSocialProxy(Type serviceRegister)
    {
        foreach (var m in serviceRegister.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "GetSocialProxy") continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[1].ParameterType == typeof(bool)) return m;
        }
        return null;
    }

    // GetSocialData(GetSocialDataRequest request, uint cancelToken): a reference-type first arg + uint second.
    // Matched by param TYPE (not arity alone) so a future same-named overload can't bind wrong.
    private static MethodInfo? FindGetSocialData(Type proxyType)
    {
        foreach (var m in proxyType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (m.Name != "GetSocialData" || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[1].ParameterType == typeof(uint) && !ps[0].ParameterType.IsValueType)
                return m;
        }
        return null;
    }

    private void WarnOnce(string msg)
    {
        if (!_warned.Add(msg)) return;
        _log.Warning($"[PlayerProfile] {msg}");
    }
}
