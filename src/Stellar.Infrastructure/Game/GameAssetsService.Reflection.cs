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
        // SPRITE path: always the string overload (profession/buff/item icons + sprite retries, shared by many
        // plugins) — byte-identical to before. TEXTURE2D path (stickers) prefers the ZResAddress overload because
        // only it funnels through loadAssetAsyncInternal's per-language remap. GRACEFUL FALLBACK — if the ZResAddress
        // overload or its string→ZResAddress converter can't be resolved, the Texture2D generic is built from the
        // string overload too (warned once), leaving _zresAddrType null so BuildTextureLoadAddress passes raw string.
        var stringOverload = ResolveStringOverload(resLoaderType);
        if (stringOverload is null)
        {
            _log.Warning("[GameAssets][icon] LoadAssetAsync<T>(string,uint,int,bool) not found on ZResLoader");
            return false;
        }
        var textureOverload = ResolveZResAddressOverload(resLoaderType);
        if (textureOverload is null)
        {
            if (!_loadAssetFallbackLogged)
            {
                _loadAssetFallbackLogged = true;
                _log.Warning("[GameAssets][icon] ZResAddress overload/converter unavailable — Texture2D loads stay on the string overload (stickers won't localize)");
            }
            textureOverload = stringOverload;
        }
        try
        {
            _loadAssetAsyncString = stringOverload.MakeGenericMethod(typeof(Sprite));
            _loadAssetAsyncTexture = textureOverload.MakeGenericMethod(typeof(Texture2D));
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
    // resolved once in ResolveStickerRowGetters().
    private MethodInfo? _stickerIdGetter;
    private MethodInfo? _stickerTypeGetter;
    private MethodInfo? _stickerGroupGetter;
    private MethodInfo? _stickerIsDefUnlockGetter;
    private MethodInfo? _stickerUnlockItemGetter;
    private bool _stickerEnumFailedLogged;
    private bool _stickerCatalogDiagLogged;   // one-shot gate for the catalog-build diagnostic line

    // Build the whole sticker catalog for the send picker. Enumerates every ChatStickersTable row (free + gated),
    // each tagged with DefaultUnlocked. Returns an EMPTY list (never throws) when the table isn't loaded yet — the
    // caller does NOT cache an empty result, so a later frame retries (game config tables load lazily at boot).
    //   Bokura.ChatStickersTableBase.GetTable(true) → ZTable<int, ChatStickersTableBase> (AutoLoad=true REQUIRED,
    //   else the backing dict is empty). We do NOT use get_Datas(): it returns the IDictionary INTERFACE, whose
    //   Il2CppInterop wrapper exposes no usable parameterless GetEnumerator, so the map-walker yields ZERO rows
    //   with no exception. Instead we read the CONCRETE base backing dict (the protected Dictionary<long,object>
    //   _datas on ITable) and walk it for its KEYS, then re-fetch each row via the proven TryGetValue (the inbound
    //   path). ⚠ Il2CppInterop surfaces that IL2CPP field as a PROPERTY, not a field — see ResolveDatas.
    internal IReadOnlyList<StickerInfo> BuildSendableStickers()
    {
        var result = new List<StickerInfo>();
        _stickerTableType ??= StellarInterop.FindType("Bokura.ChatStickersTableBase");
        if (_stickerTableType is null) return result;
        _stickerGetTable ??= StellarInterop.FindMethod(_stickerTableType, "GetTable", 1);
        if (_stickerGetTable is null) return result;
        try
        {
            var table = _stickerGetTable.Invoke(null, new object[] { true });  // AutoLoad=true REQUIRED (else _datas empty)
            if (table is null) return result;
            // Resolve the concrete base backing dict as a PROPERTY (IL2CPP field → interop property) — see ResolveDatas.
            var datas = ResolveDatas(table, out var via);
            _stickerTryGetValue ??= StellarInterop.FindMethod(table.GetType(), "TryGetValue", 3);
            bool ready = datas is not null && _stickerTryGetValue is not null && ResolveStickerRowGetters();

            int keyCount = ready ? EnumerateStickerRows(table, datas!, result) : 0;
            LogCatalogDiagOnce(table, via, datas, keyCount, result.Count);
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

    // Walk the _datas backing dict for its KEYS only, then re-fetch each row through the PROVEN TryGetValue(id, out
    // row, false) so the getters run on a correctly-typed row wrapper. The raw _datas value is boxed as
    // Il2CppSystem.Object — invoking a getter on it would throw, so we never read getters off it directly. Returns
    // the count of keys the walker yielded (for the diagnostic); appends each readable, non-gated-out row to result.
    private int EnumerateStickerRows(object table, object datas, List<StickerInfo> result)
    {
        int keyCount = 0;
        foreach (var (k, _) in PandaInventoryPullReader.EnumerateMapEntries(datas))
        {
            keyCount++;
            int id = PandaInventoryPullReader.AsInt32(k);
            if (id <= 0) continue;
            var args = new object?[] { id, null, false };   // (key, out row, errorWhenNotFound: false) — mirrors inbound path
            if (_stickerTryGetValue!.Invoke(table, args) is not true || args[1] is null) continue;
            if (TryReadStickerRow(args[1]!, out var info)) result.Add(info);
        }
        return keyCount;
    }

    // Resolve the concrete ZTable row store (base ITable `_datas`, a Dictionary<long,object>) off the table.
    // ⚠ Il2CppInterop surfaces IL2CPP INSTANCE FIELDS as managed PROPERTIES — the dump lists `_datas` as a field
    // on base ITable, but in the generated interop assembly it's a PROPERTY (that's why every row getter in this
    // file uses FindPropertyUp, never FindFieldUp). FindFieldUp("_datas") was the original bug (always null →
    // enumeration never ran). So resolve the property getter first; keep the field lookup only as a harmless
    // secondary fallback. Reports how the value was obtained via `via` (prop / field / none) for the diagnostic.
    private object? ResolveDatas(object table, out string via)
    {
        via = "none";
        var getter = StellarInterop.FindPropertyUp(table.GetType(), "_datas")?.GetGetMethod(nonPublic: true);
        if (getter is not null)
        {
            try { if (getter.Invoke(table, null) is { } d) { via = "prop"; return d; } }
            catch { /* fall through to the field fallback */ }
        }
        var field = StellarInterop.FindFieldUp(table.GetType(), "_datas");
        if (field is not null)
        {
            try { if (field.GetValue(table) is { } d) { via = "field"; return d; } }
            catch { /* give up — reported as via=none */ }
        }
        return null;
    }

    // One-shot catalog diagnostic (fires on the first call that got past the table-null gate). We're iterating
    // blind on interop reflection, so a SECOND miss must be self-explanatory: this single line reports how `_datas`
    // was obtained (prop/field/none), its runtime type, whether that type has the parameterless GetEnumerator the
    // map-walker needs, and keys-walked vs StickerInfos-built. If `_datas` is STILL null it also dumps the candidate
    // member names so we learn the real accessor.
    private void LogCatalogDiagOnce(object table, string via, object? datas, int keys, int built)
    {
        if (_stickerCatalogDiagLogged) return;
        _stickerCatalogDiagLogged = true;
        string datasType = datas?.GetType().FullName ?? "null";
        bool getEnum = datas is not null && HasParameterlessGetEnumerator(datas.GetType());
        string tail = datas is null ? $" members=[{DumpDataMemberNames(table.GetType())}]" : string.Empty;
        _log.Info($"[GameAssets][sticker] catalog: via={via} datasType={datasType} getEnum={getEnum} keys={keys} built={built}{tail}");
    }

    // True if the type exposes a parameterless GetEnumerator (public or non-public) — the probe the map-walker
    // itself uses to decide whether it can walk the dict. The concrete Dictionary<long,object> has one; the
    // get_Datas() interface wrapper does not (which is why we read the backing field, not the interface).
    private static bool HasParameterlessGetEnumerator(Type t)
    {
        foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.Name == "GetEnumerator" && m.GetParameters().Length == 0) return true;
        return false;
    }

    // Dump every PROPERTY/FIELD name (and prop/field tag) on the type and each base up to object whose name (case-
    // insensitive) contains "data"/"dict"/"map" or equals "Keys"/"Values". Only invoked when `_datas` resolved to
    // null — the output names the real backing-store accessor so a second miss isn't another blind guess.
    private static string DumpDataMemberNames(Type type)
    {
        const BindingFlags f = BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var names = new List<string>();
        for (var cur = type; cur is not null && cur != typeof(object); cur = cur.BaseType)
        {
            foreach (var p in cur.GetProperties(f)) if (IsDataMemberName(p.Name)) names.Add($"{p.Name}(prop)");
            foreach (var fi in cur.GetFields(f)) if (IsDataMemberName(fi.Name)) names.Add($"{fi.Name}(field)");
        }
        return string.Join(",", names);
    }

    private static bool IsDataMemberName(string name) =>
        name.Equals("Keys", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Values", StringComparison.OrdinalIgnoreCase)
        || name.IndexOf("data", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("dict", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("map", StringComparison.OrdinalIgnoreCase) >= 0;

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

    // Resolve a sticker config_id → its raw resource name (ChatStickersTableBase.Res, e.g. "sticker_17_11").
    // Returns null (never throws) when the config type isn't loaded yet, the id is unknown, or Res is empty —
    // the caller treats null as "not resolved" and does NOT cache it, so a later frame retries (game config
    // tables load lazily at boot). The load ADDRESS (base vs per-language localized directory) is built from
    // this Res by BuildStickerAddresses (StickerLoc.cs) — the Res itself is language-neutral.
    private string? TryResolveStickerRes(int configId)
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
            return string.IsNullOrEmpty(res) ? null : res;
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
