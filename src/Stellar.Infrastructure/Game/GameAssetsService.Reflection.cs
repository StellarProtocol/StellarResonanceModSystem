using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
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

    private const int MaxResolveRetries = 5;

    // Bounded, backoff-gated reflection lookup. Caches success permanently; a
    // failed attempt (e.g. ZResLoader singleton not registered yet during boot)
    // retries up to MaxResolveRetries times before giving up permanently.
    private bool ResolveOnce()
    {
        if (_resolveSucceeded) return true;                              // cached success — never re-resolve
        if (_resolveAttempts >= MaxResolveRetries)
        {
            if (!_resolveGaveUpLogged) { _resolveGaveUpLogged = true; _log.Warning("[GameAssets][icon] resolve failed permanently after retries"); }
            return false;
        }
        if (UnityEngine.Time.frameCount < _resolveNextRetryFrame) return false;   // backoff
        _resolveAttempts++;
        _resolveNextRetryFrame = UnityEngine.Time.frameCount + RetryBackoffFrames * _resolveAttempts;

        if (!FindRequiredTypes(out var resLoaderType, out var singletonOpenType, out var cancelSourceType)) return false;
        if (!RentCancelSource(cancelSourceType!)) return false;
        if (!ResolveLoaderInstance(resLoaderType!, singletonOpenType!)) return false;
        if (!ResolveLoadAssetMethod(resLoaderType!)) return false;
        if (!ResolveUniTaskReflection(resLoaderType!)) return false;

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
        // Idempotent: ResolveOnce may retry after a later step fails; don't re-rent (would orphan the prior rent).
        if (_cancelSourceInstance != null) return true;
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

    // --- Chat sticker (picture-emoji) table reflection --------------------------------------------
    // Bokura.ChatStickersTableBase.GetTable(true) → Bokura.Table.ZTable<int, ChatStickersTableBase>;
    // TryGetValue(configId, out row, errorWhenNotFound) → row.Res (sprite/texture name).
    // ⚠ TryGetValue's errorWhenNotFound defaults to TRUE in-game (logs on a miss) — we pass false so an
    // unknown sticker id fails silently. Each metadata handle is resolved once and cached.
    private Type? _stickerTableType;          // Bokura.ChatStickersTableBase
    private MethodInfo? _stickerGetTable;     // static GetTable(bool)
    private MethodInfo? _stickerTryGetValue;  // ZTable<int,T>.TryGetValue(int, out T, bool)
    private MethodInfo? _stickerResGetter;    // row get_Res()
    private bool _stickerResolveFailedLogged;

    // Catalog-enumeration getters (GetSendableStickers). Res reuses _stickerResGetter above; the rest are
    // resolved once in ResolveStickerRowGetters(). _stickerDatasGetter is the ZTable<int,T>.get_Datas() iterator.
    private MethodInfo? _stickerDatasGetter;
    private MethodInfo? _stickerIdGetter;
    private MethodInfo? _stickerTypeGetter;
    private MethodInfo? _stickerGroupGetter;
    private MethodInfo? _stickerIsDefUnlockGetter;
    private MethodInfo? _stickerUnlockItemGetter;
    private bool _stickerEnumFailedLogged;

    // Build the whole sticker catalog for the send picker. Enumerates every ChatStickersTable row (free + gated),
    // each tagged with DefaultUnlocked. Returns an EMPTY list (never throws) when the table isn't loaded yet — the
    // caller does NOT cache an empty result, so a later frame retries (game config tables load lazily at boot).
    //   Bokura.ChatStickersTableBase.GetTable(true)  → ZTable<int, ChatStickersTableBase> (AutoLoad=true REQUIRED,
    //   else the backing dict is empty) → get_Datas() → IDictionary<int, row>; walk its values (typed enumerator,
    //   via the proven PandaInventoryPullReader map-walker) and read each row's scalar getters.
    internal IReadOnlyList<StickerInfo> BuildSendableStickers()
    {
        var result = new List<StickerInfo>();
        _stickerTableType ??= StellarInterop.FindType("Bokura.ChatStickersTableBase");
        if (_stickerTableType is null) return result;
        _stickerGetTable ??= StellarInterop.FindMethod(_stickerTableType, "GetTable", 1);
        if (_stickerGetTable is null) return result;
        try
        {
            var table = _stickerGetTable.Invoke(null, new object[] { true });  // AutoLoad=true REQUIRED (else Datas empty)
            if (table is null) return result;
            _stickerDatasGetter ??= StellarInterop.FindMethod(table.GetType(), "get_Datas", 0);
            var datas = _stickerDatasGetter?.Invoke(table, null);
            if (datas is null || !ResolveStickerRowGetters()) return result;

            foreach (var rowObj in PandaInventoryPullReader.EnumerateMapValues(datas))
                if (rowObj is not null && TryReadStickerRow(rowObj, out var info)) result.Add(info);
        }
        catch (Exception ex)
        {
            result.Clear();   // partial list is misleading — an exception mid-walk means "not ready", retry clean
            if (!_stickerEnumFailedLogged)
            {
                _stickerEnumFailedLogged = true;
                _log.Warning($"[GameAssets][sticker] catalog enumerate threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return result;
    }

    // Read one ChatStickersTableBase row into a StickerInfo. Returns false for a junk/empty row (id <= 0).
    private bool TryReadStickerRow(object rowObj, out StickerInfo info)
    {
        info = default!;
        int id = ReadStickerInt(_stickerIdGetter, rowObj);
        if (id <= 0) return false;
        var res = _stickerResGetter!.Invoke(rowObj, null) as string ?? string.Empty;
        int isDefUnlock = ReadStickerInt(_stickerIsDefUnlockGetter, rowObj);
        int unlockItem = ReadStickerInt(_stickerUnlockItemGetter, rowObj);
        // Gated only when BOTH IsDefUnlock>0 AND UnlockItem>0 (the client blocks a send via GetChatEmojiUnlock);
        // everything else is default-unlocked / freely sendable. Resolve the quirk here so plugins never see it.
        bool defaultUnlocked = !(isDefUnlock > 0 && unlockItem > 0);
        info = new StickerInfo(id, ReadStickerInt(_stickerTypeGetter, rowObj), ReadStickerInt(_stickerGroupGetter, rowObj), defaultUnlocked, res);
        return true;
    }

    // Resolve + cache the row scalar getters (Res reuses _stickerResGetter). Returns false if any getter is still
    // missing (table type not fully resolved yet) so BuildSendableStickers retries on a later frame.
    private bool ResolveStickerRowGetters()
    {
        _stickerResGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "Res")?.GetGetMethod(nonPublic: true);
        _stickerIdGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "Id")?.GetGetMethod(nonPublic: true);
        _stickerTypeGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "Type")?.GetGetMethod(nonPublic: true);
        _stickerGroupGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "GroupId")?.GetGetMethod(nonPublic: true);
        _stickerIsDefUnlockGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "IsDefUnlock")?.GetGetMethod(nonPublic: true);
        _stickerUnlockItemGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "UnlockItem")?.GetGetMethod(nonPublic: true);
        return _stickerResGetter is not null && _stickerIdGetter is not null && _stickerTypeGetter is not null
            && _stickerGroupGetter is not null && _stickerIsDefUnlockGetter is not null && _stickerUnlockItemGetter is not null;
    }

    private static int ReadStickerInt(MethodInfo? getter, object row)
    {
        if (getter is null) return 0;
        try { return PandaInventoryPullReader.AsInt32(getter.Invoke(row, null)); }
        catch { return 0; }
    }

    // Resolve a sticker config_id → ZResLoader address. Returns null (never throws) when the config type
    // isn't loaded yet, the id is unknown, or Res is empty — the caller treats null as "not resolved" and
    // does NOT cache it, so a later frame retries (game config tables load lazily at boot).
    private string? TryResolveStickerPath(int configId)
    {
        _stickerTableType ??= StellarInterop.FindType("Bokura.ChatStickersTableBase");
        if (_stickerTableType is null) return null;
        _stickerGetTable ??= StellarInterop.FindMethod(_stickerTableType, "GetTable", 1);
        if (_stickerGetTable is null) return null;
        try
        {
            var table = _stickerGetTable.Invoke(null, new object[] { true });
            if (table is null) return null;                            // table not populated yet
            // TryGetValue lives on the closed ZTable<int,T> returned by GetTable — probe it off the instance.
            _stickerTryGetValue ??= StellarInterop.FindMethod(table.GetType(), "TryGetValue", 3);
            _stickerResGetter ??= StellarInterop.FindPropertyUp(_stickerTableType, "Res")?.GetGetMethod(nonPublic: true);
            if (_stickerTryGetValue is null || _stickerResGetter is null) return null;

            var args = new object?[] { configId, null, false };        // (key, out row, errorWhenNotFound: false)
            if (_stickerTryGetValue.Invoke(table, args) is not true || args[1] is null) return null;  // unknown id
            var res = _stickerResGetter.Invoke(args[1], null) as string;
            return string.IsNullOrEmpty(res) ? null : "ui/textures/chat_emoji/" + res;
        }
        catch (Exception ex)
        {
            if (!_stickerResolveFailedLogged)
            {
                _stickerResolveFailedLogged = true;
                _log.Warning($"[GameAssets][sticker] resolve threw for configId={configId}: {ex.GetType().Name}: {ex.Message}");
            }
            return null;
        }
    }
}
