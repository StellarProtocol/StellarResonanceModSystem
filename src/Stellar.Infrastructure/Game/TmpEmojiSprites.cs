using System;
using TMPro;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The GAME's TMP sprite asset for inline chat emoji (<c>&lt;sprite=N&gt;</c>, N=1..63), resolved once and
/// shared by every TextElement.RichEmoji text. The game draws chat emoji with TextMeshPro
/// (<c>Panda.ZUi.ZTMPText : TextMeshProUGUI</c>, label <c>lab_msg</c> under the chat bubble item) and writes the
/// 1-based index straight into TMP, so the tag is passed through UNCHANGED (no N−1 remap — that only applies to a
/// mod-built atlas). No game code sets <c>spriteAsset</c> or reads <c>TMP_Settings.defaultSpriteAsset</c>, so the
/// asset is either the TMP global default or serialized on the chat label prefab — we try the default first,
/// then copy it off a chat <c>lab_msg</c>. That label only exists once the game's chat UI has loaded/shown a
/// message, so a miss is retried LAZILY (throttled, and only while a tagged string is on screen) — never per frame.
/// Until found, TMP prints the tag literally, exactly like the plain Text path. Game-only file (TMP types): the
/// Mono UI-sandbox has no TextMeshPro package, so this must never be symlinked into it.
/// See Knowledge Base\Chat-System-Architecture.md §4 "INLINE &lt;sprite=N&gt; EMOJI RENDER".
/// </summary>
internal static class TmpEmojiSprites
{
    /// <summary>One-line outcome log (wired from <see cref="TmpStyledText.Register"/>).</summary>
    public static Action<string>? Log;

    /// <summary>Bumped once when the asset resolves; a RichEmoji handle compares it to re-apply its text.</summary>
    public static int Generation { get; private set; }

    private static TMP_SpriteAsset? _asset;
    private static float _nextProbe;
    private static bool _loggedNone, _loggedFail;

    // FindObjectsOfTypeAll<TMP_Text> walks every TMP text the game has loaded (a few ms) — fine every few
    // seconds while unresolved, never per apply. Once resolved, no probe ever runs again.
    private const float ProbeIntervalSec = 5f;

    /// <summary>The resolved asset, or null. <paramref name="probe"/> allows a throttled resolve attempt
    /// (callers pass true only when their string actually carries a <c>&lt;sprite</c> tag).</summary>
    public static TMP_SpriteAsset? Get(bool probe)
    {
        if (_asset == null && probe) TryResolve();
        return _asset;
    }

    private static void TryResolve()
    {
        var now = Time.realtimeSinceStartup;
        if (now < _nextProbe) return;
        _nextProbe = now + ProbeIntervalSec;
        try
        {
            var src = "default";
            var a = DefaultAsset();
            if (a == null) { src = "lab_msg"; a = FromChatLabel(); }
            if (a == null)
            {
                if (!_loggedNone) { _loggedNone = true; Log?.Invoke($"[Window][emoji] sprite asset source=none (no TMP default, no chat lab_msg yet) — retrying every {ProbeIntervalSec:0}s; <sprite=N> shows literally until found"); }
                return;
            }
            _asset = a;
            Generation++;
            Log?.Invoke($"[Window][emoji] sprite asset source={src} name='{a.name}' sprites={SpriteCount(a)}");
        }
        catch (Exception ex)
        {
            if (!_loggedFail) { _loggedFail = true; Log?.Invoke($"[Window][emoji] sprite asset resolve threw (will retry): {ex.Message}"); }
        }
    }

    // TMP_Settings.defaultSpriteAsset dereferences TMP_Settings.instance, which throws when the settings asset
    // isn't loaded — treat that as "no default" and fall through to the chat label.
    private static TMP_SpriteAsset? DefaultAsset()
    {
        try { return TMP_Settings.defaultSpriteAsset; }
        catch { return null; }
    }

    // A chat-bubble message label: named lab_msg, under an ancestor whose name mentions chat
    // (chat_bubble_content / mainchat_bubble_content_item), carrying a sprite asset. FindObjectsOfTypeAll (not
    // FindObjectsOfType) also sees an inactive/pooled bubble or the loaded prefab — same serialized asset — so
    // this can succeed before a bubble is on screen. Other UIs reuse the lab_msg name; the ancestor test keeps a
    // non-chat label's (different) sprite asset out.
    private static TMP_SpriteAsset? FromChatLabel()
    {
        var all = Resources.FindObjectsOfTypeAll<TMP_Text>();
        for (var i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null || t.name != "lab_msg" || !UnderChat(t.transform)) continue;
            var sa = t.spriteAsset;
            if (sa != null) return sa;
        }
        return null;
    }

    private static bool UnderChat(Transform tr)
    {
        var p = tr.parent;
        for (var depth = 0; p != null && depth < 8; depth++, p = p.parent)
            if (p.name.IndexOf("chat", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    private static int SpriteCount(TMP_SpriteAsset a)
    {
        try { return a.spriteCharacterTable?.Count ?? 0; }
        catch { return -1; }
    }
}
