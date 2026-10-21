using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// ⚠️ THROWAWAY DIAGNOSTIC — delete this file + its one call in <c>BootstrapPlugin.ApplyLifecyclePatches</c>
/// after reading the log. Pure logging; it NEVER alters a load.
///
/// <para>Question it answers: when the GAME's own chat UI renders a multi-language sticker (chat bubble picture
/// / native emoji panel — <c>chat_bubble_picture.lua</c> / <c>chat_emoji_item.lua</c> → <c>rimg:SetImageWithCallback</c>),
/// what EXACT address string does it hand the resource pipeline? Our mod requests the neutral
/// <c>ui/textures/chat_emoji/sticker_17_11</c> and gets the base (Chinese) art; this captures the game's own request
/// string to compare. Our mod renders stickers via a Unity <c>RawImage</c> through <c>GameAssetsService</c> /
/// <c>ZResLoader.LoadAssetAsync</c>, NOT via <c>ZRawImage.SetImageWithCallback</c>, so this hook sees ONLY the game's
/// native sticker loads.</para>
///
/// <para>A HarmonyX PREFIX on <c>Panda.ZUi.ZRawImage.SetImageWithCallback(ZResAddress, Action)</c> (dump.cs:140925).
/// <c>ZResAddress</c> is passed BY VALUE (a struct param, NOT <c>in</c>), so this is safe to patch — no in-struct-ref
/// trampoline crash. Falls back to <c>AsyncSetImage(ZResAddress, Action)</c> (dump.cs:140919, same by-value struct)
/// if the primary target is missing. Uses the framework's own <see cref="IHarmonyHost"/> so the id is namespaced and
/// auto-unpatches on framework teardown (no hand-rolled UnpatchSelf).</para>
/// </summary>
internal static class StickerAddressCapturePatch
{
    private static Action<string>? _log;
    private static readonly HashSet<string> _seen = new(StringComparer.Ordinal); // one log line per distinct address

    // Resolved once off the first live boxed ZResAddress runtime type (Il2CppInterop value-type wrapper).
    private static bool _memberResolved;
    private static PropertyInfo? _addrStringProp; // interop property _addressString → string (if surfaced as a prop)
    private static FieldInfo? _addrStringField;   // raw field _addressString (string) — fallback if not a property
    private static MethodInfo? _opExplicit;       // static op_Explicit(ZResAddress) → string (materializes the text)
    private static bool _extractFailLogged;       // one-shot note so a dead read doesn't leave the user guessing

    /// <summary>Mints a namespaced <see cref="Harmony"/> from the framework host, resolves the target reflectively
    /// (first param typed <c>ZResAddress</c>, identified by type NAME not namespace) and installs the prefix.
    /// Non-throwing: any miss is logged and skipped so a renamed game type never takes down framework boot.</summary>
    public static void Install(IHarmonyHost host, Action<string> log)
    {
        _log = log;
        var harmony = host.Create("stickercap");

        var type = StellarInterop.FindType("Panda.ZUi.ZRawImage");
        if (type is null) { log("[StickerCap] Panda.ZUi.ZRawImage not found; capture not installed"); return; }

        // Primary: SetImageWithCallback(ZResAddress, Action). Documented fallback: AsyncSetImage(ZResAddress, Action).
        var target = FindZResAddressMethod(type, "SetImageWithCallback")
                     ?? FindZResAddressMethod(type, "AsyncSetImage");
        if (target is null) { log("[StickerCap] no (ZResAddress, Action) method on ZRawImage; capture not installed"); return; }

        var prefix = typeof(StickerAddressCapturePatch).GetMethod(
            nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null) { log("[StickerCap] Prefix missing (build error); capture not installed"); return; }

        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            log($"[StickerCap] patched ZRawImage.{target.Name}(ZResAddress, Action) — logging distinct 'chat_emoji'/'sticker' addresses the game loads");
        }
        catch (Exception ex)
        {
            log($"[StickerCap] failed to patch ZRawImage.{target.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Unique (name, 2-arg) method whose FIRST param is ZResAddress. Matched by type NAME (not FindMethod, which is
    // count-only) so we also verify the by-value struct shape and pick the right fallback target.
    private static MethodInfo? FindZResAddressMethod(Type type, string name)
    {
        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 2) continue;
            var p0 = ps[0].ParameterType;
            if (p0.Name == "ZResAddress" || p0.FullName?.Contains("ZResAddress") == true) return m;
        }
        return null;
    }

    // __args[0] = the by-value ZResAddress (boxed by HarmonyX). Log ONCE per distinct address containing
    // "chat_emoji" or "sticker"; ignore everything else (no spam). Never throws into the game's render.
    private static void Prefix(object?[] __args)
    {
        try
        {
            if (__args is null || __args.Length < 1 || __args[0] is null) return;
            var addr = ExtractAddressString(__args[0]!);
            if (string.IsNullOrEmpty(addr)) return;
            if (addr!.IndexOf("chat_emoji", StringComparison.OrdinalIgnoreCase) < 0 &&
                addr.IndexOf("sticker", StringComparison.OrdinalIgnoreCase) < 0) return;
            if (!_seen.Add(addr)) return;
            _log?.Invoke($"[StickerCap] game loads addr='{addr}'");
        }
        catch { /* swallow — a diagnostic must never break the game's sticker render */ }
    }

    // Read the raw _addressString (interop property or field). If it is null/empty — e.g. the ZResAddress was built
    // from a ReadOnlySpan<char>/Utf16ValueStringBuilder so only _nativeString carries the text — fall back to
    // op_Explicit(ZResAddress)→string, then ToString(). First non-empty wins; all paths yield the same address text.
    private static string? ExtractAddressString(object addr)
    {
        if (!_memberResolved) ResolveMembers(addr.GetType());

        string? s = null;
        try { if (_addrStringProp is not null) s = _addrStringProp.GetValue(addr) as string; } catch { }
        if (string.IsNullOrEmpty(s))
            try { if (_addrStringField is not null) s = _addrStringField.GetValue(addr) as string; } catch { }
        if (string.IsNullOrEmpty(s))
            try { if (_opExplicit is not null) s = _opExplicit.Invoke(null, new[] { addr }) as string; } catch { }
        if (string.IsNullOrEmpty(s))
            try { s = addr.ToString(); } catch { }

        if (string.IsNullOrEmpty(s) && !_extractFailLogged)
        {
            _extractFailLogged = true;
            _log?.Invoke($"[StickerCap] could not read address string from {addr.GetType().FullName} (field/prop/op_Explicit/ToString all empty)");
        }
        return s;
    }

    // Resolve the string accessors off the live ZResAddress type once. Prop first (IL2CPP instance fields usually
    // surface as interop properties), then the raw field; op_Explicit(ZResAddress)→string as the text materializer.
    private static void ResolveMembers(Type t)
    {
        _memberResolved = true;
        try
        {
            _addrStringProp = t.GetProperty("_addressString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_addrStringProp is not null && _addrStringProp.PropertyType != typeof(string)) _addrStringProp = null;

            _addrStringField = t.GetField("_addressString",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (_addrStringField is not null && _addrStringField.FieldType != typeof(string)) _addrStringField = null;

            _opExplicit = t.GetMethod("op_Explicit",
                BindingFlags.Public | BindingFlags.Static, null, new[] { t }, null);
            if (_opExplicit is not null && _opExplicit.ReturnType != typeof(string)) _opExplicit = null;
        }
        catch { /* best-effort — extraction falls back to ToString() */ }
    }
}
