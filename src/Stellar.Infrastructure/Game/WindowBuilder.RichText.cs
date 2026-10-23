using Il2CppInterop.Runtime;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// TextElement.RichEmoji support: host body text in the game's own rich-text renderer so inline <sprite=N>
// tags draw as real emoji images instead of literal text. The game renders chat emoji with a CUSTOM uGUI
// component — Panda.ZUi.ZText : UnityEngine.UI.Text (it overrides OnPopulateMesh/PopulateEmojiLinkMesh and
// pulls glyphs from EmojiMgr) — NOT TextMeshPro, so there is no reusable TMP sprite asset. Because ZText is a
// Text, once added it configures/binds exactly like a plain Text; we only have to construct the right
// component. ZText lives in Panda.Script.dll, which the framework does NOT reference (it refs only Unity +
// Il2Cpp core), so we resolve the type by name and add it by IL2CPP type — the same mechanism the renderer
// already uses for other unreferenced game components (CinemachineCameraBackend, ZRenderLookBackend, LampCalls).
internal sealed partial class WindowBuilder
{
    // Resolved once, process-wide (the game type is global). Null until the first in-game resolve, and stays
    // null in the Mono sandbox. StellarInterop.FindType does not cache misses, so a sandbox / pre-load call
    // just re-probes cheaply and returns null; this is only called when a RichEmoji row is first built (not per
    // frame), and chat windows build only in the World phase where ZText has long since loaded.
    private static Il2CppSystem.Type? _zTextIl2Cpp;

    // Try to add a Panda.ZUi.ZText to go and return it as its Text base. Returns null — so BuildText falls back
    // to a plain Text — whenever RichEmoji can't be honoured: the type isn't present (sandbox / not yet loaded),
    // or any interop step throws. Defensive by contract: text rendering must never break; the worst case is the
    // literal <sprite=N> showing, exactly as today.
    private static Text? TryBuildRichEmojiText(GameObject go)
    {
        try
        {
            var zt = _zTextIl2Cpp;
            if (zt == null)
            {
                var managed = StellarInterop.FindType("Panda.ZUi.ZText");
                if (managed == null) return null;            // sandbox / Panda.Script not loaded → plain Text
                zt = _zTextIl2Cpp = Il2CppType.From(managed);
            }
            var comp = go.AddComponent(zt);
            // ZText : Text, so TryCast succeeds in-game; TryCast (not Cast) so a surprise mismatch degrades to
            // plain text rather than throwing out of the build.
            return comp == null ? null : comp.TryCast<Text>();
        }
        catch
        {
            return null;   // any Il2CppInterop failure (resolve / AddComponent / cast) → caller builds plain Text
        }
    }
}
