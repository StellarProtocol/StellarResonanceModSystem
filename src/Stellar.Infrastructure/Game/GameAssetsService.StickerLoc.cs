using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TEXTURE2D (chat-sticker) per-language localization for <see cref="GameAssetsService"/>. Text-bearing sticker
/// packs ship one texture per client language, each in a DIFFERENT bundle. The game does NOT remap at the catalog
/// (<c>ContentCatalogData.Localization</c> was proven to do nothing for sticker addresses — see
/// <c>Bundle-Pkg-Asset-Pipeline.md</c>). Instead it localizes a texture by REWRITING THE PATH in the UI layer
/// before the load, then loading that different address, which hashes (DJB2/Hash33) to the language-specific bundle:
/// <list type="bullet">
///   <item>Base / Simplified-Chinese (unprefixed): <c>ui/textures/chat_emoji/&lt;Res&gt;</c></item>
///   <item>Any other language: <c>ui/localizetextures/&lt;word&gt;/chat_emoji/&lt;Res&gt;</c></item>
/// </list>
/// We mirror that convention ourselves (<see cref="BuildStickerAddresses"/>), the same shape as item icons in
/// <c>items_localized_data.lua</c>. <c>&lt;word&gt;</c> is the lowercase language word from
/// <c>LocalizationMgr.GetCurrentLanguageType()</c> (<c>"english"</c>, <c>"japanese"</c>, …; <c>"chinese"</c> = base).
///
/// <para>Only SOME stickers have a localized bundle; plain face emoji exist ONLY as the base bundle. So on a
/// non-Chinese client <see cref="GameAssetsService.LoadStickerIcon"/> tries the localized address first and falls
/// back to the base address when that load misses (see the <c>Sticker</c> icon-kind fail-fast path in Retry.cs).
/// Everything here is best-effort reflection, wrapped so it can never throw into a load.</para>
///
/// <para>The load still routes through the <c>ZResAddress</c> overload (the shared TEXTURE2D path) — but purely as
/// the normal loader entry point; the per-language choice lives entirely in the address STRING we build, not in any
/// loader-side remap. The SPRITE path (<see cref="ResolveStringOverload"/>) never touches any of this.</para>
/// </summary>
internal sealed partial class GameAssetsService
{
    // --- ZResAddress localizing-overload support — TEXTURE2D path (stickers + path/skill/imagine/item icons) --------
    // When _zresAddrType is non-null, _loadAssetAsyncTexture is the ZResAddress overload's generic and texture loads
    // convert the string address via BuildTextureLoadAddress (op_Implicit(string)→ZResAddress, or the ctor fallback).
    // When null, the Texture2D generic fell back to the string overload (still works). The SPRITE path
    // (_loadAssetAsyncString) NEVER uses this — it stays on the string overload, byte-identical for other plugins.
    private Type? _zresAddrType;                      // ZResAddress struct type; null ⇒ texture path on string fallback
    private MethodInfo? _zresAddrConverter;           // op_Implicit(string)→ZResAddress
    private ConstructorInfo? _zresAddrCtor;           // ZResAddress(string) — fallback when op_Implicit is absent
    private bool _loadAssetFallbackLogged;            // one-shot gate for the fallback warning

    // --- Current-language word (LocalizationMgr) + per-outcome diagnostic ------------------------------------------
    private string? _languageWord;                    // cached lowercase word once resolved (fixed per client session)
    private MethodInfo? _getLanguageTypeMethod;       // LocalizationMgr.GetCurrentLanguageType() (instance, no args)
    private readonly HashSet<int> _stickerDiagIds = new();   // config ids already logged (dedup + cap)

    // Shape the string address for the TEXTURE2D invocation. ZResAddress path: convert via op_Implicit(string)→
    // ZResAddress (or the ctor fallback). String-overload fallback (_zresAddrType == null): the raw string. The
    // address is already fully built (base or localized) by the caller — this only picks the invocation shape.
    private object BuildTextureLoadAddress(string address)
    {
        if (_zresAddrType is null) return address;                                        // texture-path string fallback
        if (_zresAddrConverter is not null) return _zresAddrConverter.Invoke(null, new object[] { address })!;
        return _zresAddrCtor!.Invoke(new object[] { address })!;                          // ctor fallback (one is set)
    }

    // Build the (localized, base) address pair for a sticker from its raw Res name, mirroring the game's localized-
    // directory convention. `wordReady` is false until LocalizationMgr yields a word — the caller then uses the
    // base-only shape WITHOUT caching, so a later frame upgrades to the localized address (language is fixed, so once
    // resolved it never changes). HasLocalized is false for Simplified-Chinese ("chinese") and the not-ready case —
    // both load the unprefixed base bundle, the pre-change behaviour.
    private StickerAddrs BuildStickerAddresses(string res, out bool wordReady)
    {
        string baseAddr = "ui/textures/chat_emoji/" + res;
        string? word = CurrentLanguageWordOrNull();
        wordReady = !string.IsNullOrEmpty(word);
        if (!wordReady || word == "chinese")                                              // base / Simplified-Chinese
            return new StickerAddrs(baseAddr, baseAddr, word ?? string.Empty, false);
        string localized = "ui/localizetextures/" + word + "/chat_emoji/" + res;          // per-language directory
        return new StickerAddrs(localized, baseAddr, word!, true);
    }

    // Lowercase game language word from LocalizationMgr.GetCurrentLanguageType() ("english", "japanese", …;
    // "chinese" = base). LocalizationMgr extends ZSingleton<LocalizationMgr>, so GetSingleton reaches .Instance via
    // its base-chain walk. Cached once resolved — the language can't change without a client restart. Returns null
    // until the singleton is up (or on ANY reflection failure); the caller treats null as base and does NOT cache,
    // so a later call retries. Never throws into a load.
    private string? CurrentLanguageWordOrNull()
    {
        if (_languageWord is not null) return _languageWord;
        try
        {
            var mgr = StellarInterop.GetSingleton("Panda.Utility.Localization.LocalizationMgr");
            if (mgr is null) return null;                                                 // singleton not registered yet
            _getLanguageTypeMethod ??= StellarInterop.FindMethod(mgr.GetType(), "GetCurrentLanguageType", 0);
            if (_getLanguageTypeMethod is null) return null;
            // Il2CppInterop marshals the IL2CPP System.String return to a managed string; ToString() covers the rare
            // boxed-Il2CppSystem.String case. ToLowerInvariant is defensive — the game word is already lowercase.
            var raw = _getLanguageTypeMethod.Invoke(mgr, null);
            var word = raw as string ?? raw?.ToString();
            if (string.IsNullOrEmpty(word)) return null;                                  // not ready → don't cache
            return _languageWord = word!.ToLowerInvariant();
        }
        catch { return null; }
    }

    // One-shot-per-sticker-id diagnostic confirming the localized-directory mechanism in-game: did the current
    // language's localized bundle resolve (HIT), or did we fall back to the base bundle (MISS→base)? Dev-facing log
    // ONLY (English — not user-facing, no locale catalog), deduped by config id and capped so it can never spam.
    // Fires only for clients that actually attempt a localized address (non-zh / word resolved).
    private void LogStickerOutcomeOnce(int configId, string word, bool hit)
    {
        if (_stickerDiagIds.Count >= 20 || !_stickerDiagIds.Add(configId)) return;
        _log.Info($"[GameAssets][icon] sticker {configId} lang='{word}' localized={(hit ? "HIT" : "MISS→base")}");
    }

    // Find the 4-param generic LoadAssetAsync whose FIRST parameter is ZResAddress (the same overload
    // ZRawImage.AsyncSetImage uses) and resolve its string→ZResAddress converter. On success caches _zresAddrType +
    // the converter (op_Implicit(string) or a ZResAddress(string) ctor) and returns the OPEN generic method; returns
    // null (leaving _zresAddrType null) if the overload or a usable converter is missing, so the caller falls back to
    // the string overload. (Both the base and localized sticker addresses hash to the right bundle through this
    // overload — their realHashes are not catalog localization keys, so the loader passes them through unchanged.)
    private MethodInfo? ResolveZResAddressOverload(Type resLoaderType)
    {
        MethodInfo? open = null;
        Type? addrType = null;
        foreach (var m in resLoaderType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "LoadAssetAsync" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 4) continue;
            var p0 = ps[0].ParameterType;                                     // identify by type NAME, not namespace
            if (p0.Name != "ZResAddress" && p0.FullName?.Contains("ZResAddress") != true) continue;
            open = m; addrType = p0; break;
        }
        if (open is null || addrType is null) return null;

        var converter = addrType.GetMethod("op_Implicit",
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        if (converter is not null && converter.ReturnType == addrType)
            { _zresAddrType = addrType; _zresAddrConverter = converter; _zresAddrCtor = null; return open; }

        var ctor = addrType.GetConstructor(
            BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
        if (ctor is not null)
            { _zresAddrType = addrType; _zresAddrConverter = null; _zresAddrCtor = ctor; return open; }

        return null;   // overload present but no string converter → fall back to the string overload
    }

    // FALLBACK / SPRITE path: the original UniTask<T> LoadAssetAsync<T>(string,uint,int,bool) — base asset, no remap.
    private static MethodInfo? ResolveStringOverload(Type resLoaderType)
    {
        foreach (var m in resLoaderType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != "LoadAssetAsync" || !m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length == 4 && ps[0].ParameterType == typeof(string)) return m;
        }
        return null;
    }

    // The two candidate load addresses for a sticker plus the language word that chose them. Localized == Base when
    // HasLocalized is false (Simplified-Chinese / word-not-ready) — a single base attempt covers that case.
    private readonly record struct StickerAddrs(string Localized, string Base, string Word, bool HasLocalized);
}
