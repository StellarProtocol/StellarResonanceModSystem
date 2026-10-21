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
/// / native emoji panel — <c>chat_bubble_picture.lua</c> / <c>chat_emoji_item.lua</c>), what EXACT address string
/// does it hand the resource pipeline? Our mod requests the neutral <c>ui/textures/chat_emoji/sticker_17_11</c> and
/// gets the base (Chinese) art; this captures the game's own request string to compare. Our mod renders stickers via
/// a Unity <c>RawImage</c> through <c>GameAssetsService</c> / <c>ZResLoader.LoadAssetAsync</c>, NOT via
/// <c>ZRawImage</c>, so this hook sees ONLY the game's native sticker loads.</para>
///
/// <para>HarmonyX PREFIXES on BOTH of <c>Panda.ZUi.ZRawImage</c>'s image setters:
/// <c>SetImage(ZResAddress)</c> (dump.cs:140925, RVA 0x4CE3710 — the emoji PANEL grid) and
/// <c>SetImageWithCallback(ZResAddress, Action)</c> (dump.cs:140925+, RVA 0x4CE3760 — the chat BUBBLE).
/// <c>ZResAddress</c> is passed BY VALUE on each (a struct param, NOT <c>in</c>), so both are safe to patch — no
/// in-struct-ref trampoline crash. <c>AsyncSetImage(ZResAddress, Action)</c> remains the documented fallback for the
/// 2-arg target. Uses the framework's own <see cref="IHarmonyHost"/> so the id is namespaced and auto-unpatches on
/// framework teardown (no hand-rolled UnpatchSelf).</para>
///
/// <para>UNFILTERED on purpose: whatever the native sticker address string turns out to be, we must see it, so there
/// is NO 'chat_emoji'/'sticker' substring filter. Dedup keeps the log readable (one line per distinct address) and a
/// hard cap of 120 distinct entries stops a pathological scene from spamming the log.</para>
/// </summary>
internal static class StickerAddressCapturePatch
{
    private const int MaxDistinct = 120; // hard cap on logged distinct addresses so a busy scene can't run away

    private static Action<string>? _log;
    private static readonly HashSet<string> _seen = new(StringComparer.Ordinal); // one log line per distinct address

    // Resolved once off the first live boxed ZResAddress runtime type (Il2CppInterop value-type wrapper).
    private static bool _memberResolved;
    private static PropertyInfo? _addrStringProp; // interop property _addressString → string (if surfaced as a prop)
    private static FieldInfo? _addrStringField;   // raw field _addressString (string) — fallback if not a property
    private static MethodInfo? _opExplicit;       // static op_Explicit(ZResAddress) → string (materializes the text)
    private static bool _extractFailLogged;       // one-shot note so a dead read doesn't leave the user guessing

    /// <summary>Mints a namespaced <see cref="Harmony"/> from the framework host, resolves BOTH ZResAddress setters
    /// reflectively (first param typed <c>ZResAddress</c>, identified by type NAME not namespace) and installs a
    /// prefix on each. Non-throwing: any miss is logged and skipped so a renamed game type never takes down
    /// framework boot.</summary>
    public static void Install(IHarmonyHost host, Action<string> log)
    {
        _log = log;
        var harmony = host.Create("stickercap");

        var type = StellarInterop.FindType("Panda.ZUi.ZRawImage");
        if (type is null) { log("[StickerCap] Panda.ZUi.ZRawImage not found; capture not installed"); return; }

        // SetImage(ZResAddress) = emoji panel grid (1 arg); SetImageWithCallback(ZResAddress, Action) = chat bubble
        // (2 args), with AsyncSetImage(ZResAddress, Action) as the documented fallback. Both pass ZResAddress by value.
        var setImage = FindZResAddressMethod(type, "SetImage", 1);
        var withCallback = FindZResAddressMethod(type, "SetImageWithCallback", 2)
                           ?? FindZResAddressMethod(type, "AsyncSetImage", 2);

        var siPrefix = GetPrefix(nameof(PrefixSetImage));
        var cbPrefix = GetPrefix(nameof(PrefixSetImageWithCallback));

        var patched = new List<string>(2);
        TryPatch(harmony, setImage, siPrefix, "SetImage(ZResAddress)", patched);
        TryPatch(harmony, withCallback, cbPrefix,
            $"{withCallback?.Name}(ZResAddress, Action)", patched);

        if (patched.Count == 0)
        {
            log("[StickerCap] no patchable ZRawImage image setter found; capture not installed");
            return;
        }
        log($"[StickerCap] patched ZRawImage {string.Join(" + ", patched)} — logging EVERY distinct address " +
            $"(unfiltered, dedup, cap {MaxDistinct}) the game hands each setter");
    }

    private static MethodInfo? GetPrefix(string name)
        => typeof(StickerAddressCapturePatch).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

    // Patch one target with its prefix; record a label for the confirmation line. A miss (null target/prefix) is
    // skipped silently — the "nothing patched" case is reported once by the caller — and a patch throw is logged.
    private static void TryPatch(Harmony harmony, MethodInfo? target, MethodInfo? prefix,
        string label, List<string> patched)
    {
        if (target is null || prefix is null) return;
        try
        {
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            patched.Add(label);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[StickerCap] failed to patch ZRawImage.{target.Name}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Unique method of the given name whose FIRST param is ZResAddress and which has exactly `argCount` params.
    // Matched by type NAME (not FindMethod, which is count-only) so we verify the by-value struct shape and pick the
    // right overload. `argCount` is always >= 1 for our targets, so ps[0] is safe.
    private static MethodInfo? FindZResAddressMethod(Type type, string name, int argCount)
    {
        foreach (var m in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != argCount || ps.Length == 0) continue;
            var p0 = ps[0].ParameterType;
            if (p0.Name == "ZResAddress" || p0.FullName?.Contains("ZResAddress") == true) return m;
        }
        return null;
    }

    // __args[0] = the by-value ZResAddress (boxed by HarmonyX) on BOTH overloads. Each prefix extracts the address and
    // funnels into Capture with its own method tag. Return void (don't block the original). Never throws into render.
    private static void PrefixSetImage(object?[] __args)
    {
        try
        {
            if (__args is null || __args.Length < 1 || __args[0] is null) return;
            Capture(ExtractAddressString(__args[0]!), "SetImage");
        }
        catch { /* swallow — a diagnostic must never break the game's sticker render */ }
    }

    private static void PrefixSetImageWithCallback(object?[] __args)
    {
        try
        {
            if (__args is null || __args.Length < 1 || __args[0] is null) return;
            Capture(ExtractAddressString(__args[0]!), "SetImageWithCallback");
        }
        catch { /* swallow — a diagnostic must never break the game's sticker render */ }
    }

    // Shared capture: no substring filter (see class remarks). Dedup + hard cap keep the log readable. A null/empty
    // extraction is noted ONCE (with the first caller's tag) so a dead read doesn't look like silence.
    private static void Capture(string? addr, string tag)
    {
        if (string.IsNullOrEmpty(addr))
        {
            if (!_extractFailLogged)
            {
                _extractFailLogged = true;
                _log?.Invoke($"[StickerCap] {tag}: could not read address string");
            }
            return;
        }
        if (_seen.Count >= MaxDistinct) return;   // cap reached — stop before adding, so the set never exceeds 120
        if (!_seen.Add(addr!)) return;            // already logged this exact address — one line per distinct address
        _log?.Invoke($"[StickerCap] {tag} addr='{addr}'");
    }

    // Read the raw _addressString (interop property or field). If it is null/empty — e.g. the ZResAddress was built
    // from a ReadOnlySpan<char>/Utf16ValueStringBuilder so only _nativeString carries the text — fall back to
    // op_Explicit(ZResAddress)→string, then ToString(). First non-empty wins; all paths yield the same address text.
    // Returns null/empty on total failure; the one-shot "could not read" note is owned by Capture (it has the tag).
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
