using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// WindowSpec.ContentOpacity (fw 2.33.0, opt-in): fade a window's whole content — a "foreground" opacity that leaves the
// window's background alone, so a plugin can offer Background and Foreground sliders that don't affect each other.
internal sealed partial class WindowBuilder
{
    // Names of the root children that ARE the window background, never faded by ContentOpacity
    // (BuildWindowBackdrop's GO and a top-level BackdropElement; see WindowBuilder.Panel.cs).
    private const string WindowBackdropName = "WindowBackdrop", BackdropElementName = "Backdrop";

    // One CanvasGroup per top-level child of the window root, NOT one on the root itself:
    // - the root's own Image is the background (GlassMenu frame / Borderless BackgroundOpacity blocker) and the
    //   BackgroundColor backdrop is a root child, so a root group would fade the background too;
    // - `ignoreParentGroups` on the backdrop would dodge that, but it would also dodge the prebuild stage's alpha-0
    //   group (WindowBuilder.PrebuildStage.cs) and paint a hidden prebuilt window's backdrop on screen.
    // Children with a CanvasGroup of their own are skipped (that group belongs to someone else's alpha logic).
    // Runs after the content tree is built, so the chrome's grip/✕ and the top-level elements are all covered; lazy
    // Conditional branches build INSIDE those children and inherit the fade. Unity propagates group alpha into nested
    // canvases (the meter rows' live canvases), so one alpha write fades everything below.
    private static void BindContentOpacity(WindowSpec spec, RectTransform root, WindowToken token)
    {
        if (spec.ContentOpacity is not { } opacity) return;   // default: no groups at all, exactly as before
        var groups = new System.Collections.Generic.List<CanvasGroup>(root.childCount);
        for (var i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            if (child.name is WindowBackdropName or BackdropElementName) continue;
            if (child.GetComponent<CanvasGroup>() != null) continue;
            var cg = child.gameObject.AddComponent<CanvasGroup>();
            // Explicit (these are Unity's defaults): the fade is visual only — clicks, hover and the ticker's
            // hit-tests behave exactly as without it, even at alpha 0.
            cg.blocksRaycasts = true; cg.interactable = true;
            groups.Add(cg);
        }
        var binding = new ContentOpacityBinding { Groups = groups.ToArray(), Opacity = opacity };
        binding.Apply();   // paint at build so the first frame isn't a full-opacity flash
        token.ContentOpacity = binding;
    }

    // Poll-diffed: re-reads the plugin delegate every token Apply (≤10 Hz refresh), but writes the groups only when the
    // resolved alpha changed — an alpha write re-meshes every graphic below, so a steady value must cost nothing.
    internal sealed class ContentOpacityBinding
    {
        public CanvasGroup[] Groups = System.Array.Empty<CanvasGroup>();
        public System.Func<float> Opacity = null!;
        private float _last = -1f;

        public void Apply()
        {
            var a = ContentOpacityRules.Resolve(Opacity);
            if (a == _last) return;
            for (var i = 0; i < Groups.Length; i++) if (Groups[i] != null) Groups[i].alpha = a;
            _last = a;
        }
    }
}

/// <summary>Pure rules for <see cref="WindowSpec.ContentOpacity"/> (System.Math only, no Unity → unit-testable; kept out of
/// WindowBuilder so a test never runs its Unity-touching static init).</summary>
internal static class ContentOpacityRules
{
    /// <summary>The alpha a ContentOpacity delegate resolves to: clamped to [0, 1]; a throw or NaN counts as 1 (fully
    /// visible — a broken plugin delegate must never make its window vanish).</summary>
    public static float Resolve(System.Func<float> opacity)
    {
        float v;
        try { v = opacity(); } catch { return 1f; }
        return float.IsNaN(v) ? 1f : System.Math.Clamp(v, 0f, 1f);
    }
}
