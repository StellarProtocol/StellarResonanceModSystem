using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// TEXTURE2D (chat-sticker) localization support for <see cref="GameAssetsService"/>. Chat stickers ship ~11 distinct
/// per-language textures per multi-lang id; the game localizes them AUTOMATICALLY inside the shared loader
/// <c>ZResLoader.loadAssetAsyncInternal</c> (every <c>LoadAssetAsync</c> overload funnels through it): if
/// <c>ResourceManager.CurrentLanguageIndex != 0</c> and <c>ResourceManager.Provider != null</c> it remaps the
/// address via <c>ContentCatalogData.Localization(addr, langIdx)</c> before loading the per-language variant bundle.
///
/// <para>So all we must do is route a Texture2D load through the <c>ZResAddress</c> overload with the <b>NEUTRAL</b>
/// address and let the loader localize ONCE (<see cref="BuildTextureLoadAddress"/> + the overload resolved by
/// <see cref="ResolveZResAddressOverload"/>). We must NOT pre-call <c>Localization</c> and feed a pre-remapped hash
/// to the <c>uint</c> overload — that double-localizes to null. The SPRITE path never touches any of this.</para>
///
/// <para><b>ResourceManager is a STATIC class</b> (<c>ZResource.ResourceManager</c>) — there is no instance.
/// <c>CurrentLanguageIndex</c> (static), <c>GetAddressRealHash(in ZResAddress, bool)</c> (static) and <c>Provider</c>
/// (internal static field surfaced by Il2CppInterop as a property) are reached statically off the type;
/// <c>Localization(uint, int)</c> is an instance method on the live <c>Provider</c> object. Everything here is
/// best-effort reflection, wrapped so it can never throw into a load; the only load-affecting output is the chosen
/// address shape — the diagnostic is log-only.</para>
/// </summary>
internal sealed partial class GameAssetsService
{
    // --- ZResAddress localizing-overload support — TEXTURE2D path ONLY (stickers via LoadStickerIcon) ---------------
    // When _zresAddrType is non-null, _loadAssetAsyncTexture is the ZResAddress overload's generic and texture loads
    // convert the string address via BuildTextureLoadAddress (op_Implicit(string)→ZResAddress, or the ctor fallback).
    // When null, the Texture2D generic fell back to the string overload (un-localized but working). The SPRITE path
    // (_loadAssetAsyncString) NEVER uses this — it stays on the string overload, byte-identical for other plugins.
    private Type? _zresAddrType;                      // ZResAddress struct type; null ⇒ texture path on string fallback
    private MethodInfo? _zresAddrConverter;           // op_Implicit(string)→ZResAddress
    private ConstructorInfo? _zresAddrCtor;           // ZResAddress(string) — fallback when op_Implicit is absent
    private bool _loadAssetFallbackLogged;            // one-shot gate for the fallback warning

    // --- ResourceManager handles (CurrentLanguageIndex for the cache key; the rest only for the diagnostic) --------
    private Type? _resourceManagerType;               // ZResource.ResourceManager (static class)
    private MethodInfo? _currentLanguageIndexGetter;  // static get_CurrentLanguageIndex()
    private MethodInfo? _getAddressRealHash;          // static GetAddressRealHash(in ZResAddress, bool)
    private PropertyInfo? _providerProperty;          // static Provider (il2cpp field → interop property)
    private FieldInfo? _providerField;                // static Provider as a raw field (fallback if not a property)
    private MethodInfo? _localizationMethod;          // Provider.Localization(uint, int) — resolved off the live object
    private bool _stickerLocDiagLogged;               // one-shot gate for the stickerloc2: diagnostic line

    // Shape the NEUTRAL string address for the TEXTURE2D invocation (and the diagnostic hash). ZResAddress path:
    // convert via op_Implicit(string)→ZResAddress (or the ctor fallback) so the loader applies the catalog's
    // per-language remap. String-overload fallback (_zresAddrType == null): the raw string. SPRITE never calls this.
    private object BuildTextureLoadAddress(string address)
    {
        if (_zresAddrType is null) return address;                                        // texture-path string fallback
        if (_zresAddrConverter is not null) return _zresAddrConverter.Invoke(null, new object[] { address })!;
        return _zresAddrCtor!.Invoke(new object[] { address })!;                          // ctor fallback (one is set)
    }

    // Find the 4-param generic LoadAssetAsync whose FIRST parameter is ZResAddress (the localizing overload — the
    // same one ZRawImage.AsyncSetImage uses) and resolve its string→ZResAddress converter. On success caches
    // _zresAddrType + the converter (op_Implicit(string) or a ZResAddress(string) ctor) and returns the OPEN generic
    // method; returns null (leaving _zresAddrType null) if the overload or a usable converter is missing, so the
    // caller falls back to the string overload.
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

    // Read ResourceManager.CurrentLanguageIndex (static; Il2CppInterop surfaces the backing field as a property, so
    // resolve get_CurrentLanguageIndex()). 0 on ANY failure — the loader treats langIdx 0 as "base language, no
    // remap", so an unread index degrades to exactly the pre-change (base-language) behaviour, never a broken load.
    private int CurrentLanguageIndexOrZero()
    {
        try
        {
            _resourceManagerType ??= StellarInterop.FindType("ZResource.ResourceManager");
            if (_resourceManagerType is null) return 0;
            _currentLanguageIndexGetter ??= StellarInterop
                .FindPropertyUp(_resourceManagerType, "CurrentLanguageIndex")?.GetGetMethod(nonPublic: true);
            if (_currentLanguageIndexGetter is null) return 0;
            return Convert.ToInt32(_currentLanguageIndexGetter.Invoke(null, null));
        }
        catch { return 0; }
    }

    // Resolve + cache the extra handles the stickerloc DIAGNOSTIC needs: the ZResAddress converter (reused from the
    // load path — null if we fell back to the string overload, so the diagnostic can't build a ZResAddress to hash),
    // ResourceManager.GetAddressRealHash (static, `in ZResAddress`), and the Provider accessor (il2cpp internal static
    // field → interop property, with a raw-field fallback). Localization is resolved later off the live Provider.
    private bool TryResolveStickerLocDiag()
    {
        if (_zresAddrType is null) return false;                      // no converter → can't build a ZResAddress to hash
        _resourceManagerType ??= StellarInterop.FindType("ZResource.ResourceManager");
        if (_resourceManagerType is null) return false;
        _getAddressRealHash ??= StellarInterop.FindMethod(_resourceManagerType, "GetAddressRealHash", 2);
        if (_providerProperty is null && _providerField is null)
        {
            _providerProperty = StellarInterop.FindPropertyUp(_resourceManagerType, "Provider");
            if (_providerProperty is null) _providerField = StellarInterop.FindFieldUp(_resourceManagerType, "Provider");
        }
        return _getAddressRealHash is not null && (_providerProperty is not null || _providerField is not null);
    }

    // One-shot RICH localization probe (log ONLY — never alters the load). We show the BASE sticker texture and need
    // to know WHY: is the realHash wrong, or the language index? So instead of probing whichever sticker happens to be
    // loading, this CONSTRUCTS a FIXED test address (sticker_17_11 — a proven multi-lang id) and resolves it fully, so
    // it reports the same localized case regardless of which picker category the user opens first. The incoming
    // (neutralPath, langIdx) only TRIGGER the probe; it ignores neutralPath and reads the language index live below.
    // Skips WITHOUT burning the one-shot until the loader + diag handles resolve, so a boot-race early call doesn't
    // waste the shot. Everything is wrapped — it must never throw into a load.
    //   - realHash_false / realHash_true: GetAddressRealHash(addr, forInit) for BOTH forInit values — `true` may force
    //     the records-map entry to resolve the real catalog hash instead of the DJB2 fallback, so the two can differ.
    //   - remap[false] / remap[true]: for EACH hash, sweep candidate langIdx 1..14 through Provider.Localization and
    //     report every idx that actually remaps (result != input). If a hash never remaps it is not a
    //     ContentCatalogData.localizationOffset_ key ⟹ per-language sticker selection is config/UI-driven, not native.
    private void MaybeLogStickerLoc(string neutralPath, int langIdx)
    {
        if (_stickerLocDiagLogged) return;
        const string addr = "ui/textures/chat_emoji/sticker_17_11";   // fixed test address; neutralPath/langIdx only trigger
        try
        {
            if (!ResolveOnce() || !TryResolveStickerLocDiag()) return; // not ready yet → retry on a later load (no gate)

            int langIdxLive = CurrentLanguageIndexOrZero();
            var provider = _providerProperty is not null
                ? _providerProperty.GetGetMethod(nonPublic: true)?.Invoke(null, null)
                : _providerField!.GetValue(null);

            // One boxed ZResAddress reused for both GetAddressRealHash calls — the first param is `in` (readonly ref),
            // so the invoke never mutates the box; only the `forInit` bool differs between the two calls.
            object zresAddr = BuildTextureLoadAddress(addr);
            uint realHashFalse = Convert.ToUInt32(_getAddressRealHash!.Invoke(null, new object[] { zresAddr, false }));
            uint realHashTrue = Convert.ToUInt32(_getAddressRealHash!.Invoke(null, new object[] { zresAddr, true }));

            if (provider is not null)
                _localizationMethod ??= StellarInterop.FindMethod(provider.GetType(), "Localization", 2);

            _stickerLocDiagLogged = true;
            _log.Info(
                "[GameAssets][icon] stickerloc2: addr='" + addr + "'" +
                "\n  langIdxLive=" + langIdxLive + " providerNull=" + (provider is null) +
                "\n  realHash_false=" + realHashFalse + " realHash_true=" + realHashTrue +
                "\n  " + ProbeRemap("false", provider, realHashFalse) +
                "\n  " + ProbeRemap("true", provider, realHashTrue));
        }
        catch (Exception ex)
        {
            _stickerLocDiagLogged = true;   // a single failure note is enough — don't retry-spam the diagnostic
            _log.Warning($"[GameAssets][icon] stickerloc2 diag threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Sweep candidate language index 1..14 through Provider.Localization(hash, idx) and format every idx that remaps
    // (result != input hash) as "remap[<label>]: idx=<idx>-><result>, ..." or "remap[<label>]: NONE" if none do.
    // Each Localization call is individually try/caught so a bad index can't abort the sweep or throw into the probe.
    private string ProbeRemap(string label, object? provider, uint hash)
    {
        if (provider is null || _localizationMethod is null) return $"remap[{label}]: (provider/method unavailable)";
        var hits = new List<string>();
        for (int idx = 1; idx <= 14; idx++)
        {
            try
            {
                uint result = Convert.ToUInt32(_localizationMethod.Invoke(provider, new object[] { hash, idx }));
                if (result != hash) hits.Add($"idx={idx}->{result}");
            }
            catch { /* a bad index must not abort the loop */ }
        }
        return hits.Count == 0 ? $"remap[{label}]: NONE" : $"remap[{label}]: {string.Join(", ", hits)}";
    }
}
