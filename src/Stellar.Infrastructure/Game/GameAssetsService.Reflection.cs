using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>Reflection-resolution + cancel-token + UniTask-ops helpers for <see cref="GameAssetsService"/>.</summary>
internal sealed partial class GameAssetsService
{
    // Mint a unique cancel token via ZCancelSource.CreateToken(). Returns false
    // and logs on any failure; the token is 0 only on failure.
    private bool MintCancelToken(int key, string label, out uint token)
    {
        token = 0;
        // ZCancelSource.CreateToken() returns a unique uint token per load.
        // Sharing tokens across concurrent loads makes the loader pre-cancel them.
        try
        {
            var raw = _createTokenMethod!.Invoke(_cancelSourceInstance, null);
            if (raw is uint u) token = u;
        }
        catch (Exception tokenEx)
        {
            _log.Warning($"[GameAssets][icon] CreateToken() threw for {label}={key}: {tokenEx.GetType().Name}: {tokenEx.Message}");
            return false;
        }
        if (token == 0)
        {
            _log.Warning($"[GameAssets][icon] CreateToken() returned 0 for {label}={key}");
            return false;
        }
        return true;
    }

    // One-shot reflection lookup. Returns true if all cached members are non-null.
    private bool ResolveOnce()
    {
        if (_resolveAttempted) return _resolveSucceeded;
        _resolveAttempted = true;

        if (!FindRequiredTypes(out var resLoaderType, out var singletonOpenType, out var cancelSourceType))
            return false;
        if (!RentCancelSource(cancelSourceType!))
            return false;
        if (!ResolveLoaderInstance(resLoaderType!, singletonOpenType!))
            return false;
        if (!ResolveLoadAssetMethod(resLoaderType!))
            return false;
        if (!ResolveUniTaskReflection(resLoaderType!))
            return false;

        _resolveSucceeded = true;
        return true;
    }

    private bool FindRequiredTypes(out Type? resLoaderType, out Type? singletonOpenType, out Type? cancelSourceType)
    {
        resLoaderType = null;
        singletonOpenType = null;
        cancelSourceType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                resLoaderType ??= asm.GetType("ZResource.Loader.ZResLoader", throwOnError: false);
                singletonOpenType ??= asm.GetType("ZUtil.ZSingleton`1", throwOnError: false);
                cancelSourceType ??= asm.GetType("ZUtil.ZCancelSource", throwOnError: false);
            }
            catch
            {
                // Some Il2CppInterop assemblies throw on GetType — skip.
                continue;
            }
            if (resLoaderType is not null && singletonOpenType is not null && cancelSourceType is not null) break;
        }
        if (resLoaderType is null)
            { _log.Warning("[GameAssets][icon] ZResource.Loader.ZResLoader not found in any loaded assembly"); return false; }
        if (singletonOpenType is null)
            { _log.Warning("[GameAssets][icon] ZUtil.ZSingleton`1 not found in any loaded assembly"); return false; }
        if (cancelSourceType is null)
            { _log.Warning("[GameAssets][icon] ZUtil.ZCancelSource not found in any loaded assembly"); return false; }
        return true;
    }

    // ZResLoader rejects token=0 ("invalid token") and tokens shared across
    // concurrent loads get pre-cancelled. The game's pattern is: rent a
    // single ZCancelSource and call CreateToken() once per load to mint a
    // unique uint token. We rent one source for the lifetime of the
    // plugin (these are long-lived in the game itself).
    private bool RentCancelSource(Type cancelSourceType)
    {
        try
        {
            var rentMethod = cancelSourceType.GetMethod(
                "Rent",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            _cancelSourceInstance = rentMethod?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            _log.Warning($"[GameAssets][icon] ZCancelSource.Rent() threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (_cancelSourceInstance is null)
        {
            _log.Warning("[GameAssets][icon] ZCancelSource.Rent() returned null");
            return false;
        }
        _createTokenMethod = cancelSourceType.GetMethod(
            "CreateToken",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: Type.EmptyTypes, modifiers: null);
        if (_createTokenMethod is null)
        {
            _log.Warning("[GameAssets][icon] ZCancelSource.CreateToken() not found");
            return false;
        }
        return true;
    }

    private bool ResolveLoaderInstance(Type resLoaderType, Type singletonOpenType)
    {
        try
        {
            var closed = singletonOpenType.MakeGenericType(resLoaderType);
            var instanceProp = closed.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            _loaderInstance = instanceProp?.GetValue(null);
        }
        catch (Exception ex)
        {
            _log.Warning($"[GameAssets][icon] reading ZSingleton<ZResLoader>.Instance threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        if (_loaderInstance is null)
        {
            _log.Warning("[GameAssets][icon] ZSingleton<ZResLoader>.Instance was null (loader not registered yet)");
            return false;
        }
        return true;
    }

    private bool ResolveLoadAssetMethod(Type resLoaderType)
    {
        // Find: UniTask<TObject> LoadAssetAsync<TObject>(string, uint, int, bool)
        MethodInfo? openMethod = null;
        foreach (var m in resLoaderType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "LoadAssetAsync") continue;
            if (!m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 4) continue;
            if (ps[0].ParameterType != typeof(string)) continue;
            openMethod = m;
            break;
        }
        if (openMethod is null)
        {
            _log.Warning("[GameAssets][icon] LoadAssetAsync<T>(string,uint,int,bool) not found on ZResLoader");
            return false;
        }
        try
        {
            _loadAssetAsyncString = openMethod.MakeGenericMethod(typeof(Sprite));
            _loadAssetAsyncTexture = openMethod.MakeGenericMethod(typeof(Texture2D));
        }
        catch (Exception ex)
        {
            _log.Warning($"[GameAssets][icon] MakeGenericMethod threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        return true;
    }

    // Resolve (and cache) Status/GetAwaiter/GetResult for a concrete UniTask<T> instance — works for both
    // UniTask<Sprite> and UniTask<Texture2D> (each is a distinct closed generic with its own members).
    private (MethodInfo? Status, MethodInfo? Awaiter, MethodInfo? GetResult) UniTaskOps(object uniTask)
    {
        var t = uniTask.GetType();
        if (_uniTaskOps.TryGetValue(t, out var ops)) return (ops.Status, ops.Awaiter, ops.GetResult);
        const BindingFlags f = BindingFlags.Instance | BindingFlags.Public;
        var status = t.GetProperty("Status", f)?.GetGetMethod();
        var awaiter = t.GetMethod("GetAwaiter", f, binder: null, types: Type.EmptyTypes, modifiers: null);
        var getResult = awaiter?.ReturnType.GetMethod("GetResult", f, binder: null, types: Type.EmptyTypes, modifiers: null);
        if (status is not null && awaiter is not null && getResult is not null)
            _uniTaskOps[t] = (status, awaiter, getResult);
        return (status, awaiter, getResult);
    }

    private bool ResolveUniTaskReflection(Type resLoaderType)
    {
        // UniTask<Sprite>.Status and GetAwaiter() and Awaiter.GetResult().
        // Probe via the return type so we don't have to guess the assembly.
        var unitaskType = _loadAssetAsyncString!.ReturnType; // UniTask<Sprite>
        _unitaskStatusGetter = unitaskType.GetProperty("Status", BindingFlags.Instance | BindingFlags.Public)?.GetGetMethod();
        _unitaskGetAwaiter = unitaskType.GetMethod("GetAwaiter", BindingFlags.Instance | BindingFlags.Public, binder: null, types: Type.EmptyTypes, modifiers: null);

        if (_unitaskGetAwaiter is not null)
        {
            var awaiterType = _unitaskGetAwaiter.ReturnType; // UniTask<T>.Awaiter
            _awaiterGetResult = awaiterType.GetMethod("GetResult", BindingFlags.Instance | BindingFlags.Public, binder: null, types: Type.EmptyTypes, modifiers: null);
        }

        if (_unitaskStatusGetter is null || _unitaskGetAwaiter is null || _awaiterGetResult is null)
        {
            _log.Warning(
                $"[GameAssets][icon] UniTask reflection incomplete: " +
                $"Status={_unitaskStatusGetter is not null} GetAwaiter={_unitaskGetAwaiter is not null} GetResult={_awaiterGetResult is not null}");
            return false;
        }

        _log.Info(
            $"[GameAssets][icon] reflection ready (loader={resLoaderType.FullName}, unitask={unitaskType.FullName}, " +
            $"cancelSource=rented)");
        return true;
    }
}
