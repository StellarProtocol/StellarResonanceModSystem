using Stellar.Infrastructure.UI;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Rail-button icon visuals for <see cref="PandaUGuiAdapter"/>: the icon images, the per-frame glow pulse
/// and the polled hover animation (kept in a sibling partial so .Build stays under the 500-line cap).
/// </summary>
internal sealed partial class PandaUGuiAdapter
{
    // Rail buttons we animate each frame: pulse the accent glow halo + apply a
    // manual hover wash (the game's InputSystem EventSystem doesn't dispatch
    // PointerEnter/Exit to our injected button, so uGUI ColorTint never fired —
    // we poll the pointer against the cell rect instead). Destroyed entries
    // (menu close) are pruned in TickGlow.
    private sealed class RailVisual
    {
        public RawImage? Glow;     // accent halo (pulsed + brightened on hover); null on plugin (unbranded) buttons
        public RawImage? Star;     // the icon: accent Stellar star, or a plugin's untinted PNG (scaled on hover); null for glyph/label-only
        public Image Surface = null!;       // transparent click surface (kept for liveness check)
        public RectTransform Rect = null!;  // cell rect (hover hit-test)
        public Canvas? Canvas;              // owning canvas, resolved once at build (hover hit-test camera)
        public float Scale = 1f;            // current hover scale (lerped)
    }
    private readonly System.Collections.Generic.List<RailVisual> _railVisuals = new();
    private RawImage? _pendingGlow;   // set by AddGlowingRailIcon during a build, read into the RailVisual
    private RawImage? _pendingStar;

    // The rail icon as a glowing, theme-accent star (matches the launcher menu
    // logo): a soft accent halo (stellar-glow, pulsed each frame in TickGlow)
    // behind an accent-tinted crisp star. Both centred at normalised height
    // <paramref name="centerY"/>; neither raycasts (the cell surface owns the click).
    private void AddGlowingRailIcon(Transform parent, Texture iconTex, float side, float centerY)
    {
        var a = _theme.Colors.MenuAccent;
        var glowTex = _iconCache.Get(LauncherIcons.Get("stellar-glow"));
        if (glowTex != null)
            _pendingGlow = AddRawImage(parent, glowTex, side * 1.8f, centerY, new Color(a.R, a.G, a.B, 0.3f));
        _pendingStar = AddRawImage(parent, iconTex, side, centerY, new Color(a.R, a.G, a.B, 1f));
    }

    // A plugin's rail icon: the PNG as-is (white multiplier = untinted), no halo, so a game-style grey
    // icon matches the native rail items. Same size/centre as the branded star, and it gets the same
    // hover scale-up in TickGlow (Glow stays null, so there's no pulse).
    private void AddPlainRailIcon(Transform parent, Texture iconTex, float side, float centerY)
        => _pendingStar = AddRawImage(parent, iconTex, side, centerY, new Color(1f, 1f, 1f, 1f));

    // A square RawImage centred horizontally at normalised height <paramref name="centerY"/>.
    // RawImage takes the Texture2D directly (no Sprite); doesn't raycast.
    private static RawImage AddRawImage(Transform parent, Texture tex, float side, float centerY, Color color)
    {
        var go = new GameObject("Icon");
        go.AddComponent<CanvasRenderer>();
        var img = go.AddComponent<RawImage>();
        img.texture = tex;
        img.color = color;
        img.raycastTarget = false;
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.GetComponent<RectTransform>();
        // Anchor at (0.5, centerY) with a CENTRED pivot so the element's centre
        // sits there — glow + star (different sizes) stay concentric.
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, centerY);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = new Vector2(side, side);
        rt.localScale = Vector3.one;
        return img;
    }

    /// <summary>
    /// Per-frame (from Host.Update): pulse each rail glow halo in the theme accent,
    /// and on hover ANIMATE THE ICON ONLY — the star + halo scale up and the halo
    /// brightens (no background highlight). Hover is polled against the cell rect
    /// because the InputSystem EventSystem doesn't send PointerEnter/Exit to our
    /// injected uGUI button.
    /// </summary>
    public void TickGlow()
    {
        if (_railVisuals.Count == 0) return;
        var a = _theme.Colors.MenuAccent;
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * 2.6f);

        for (int i = _railVisuals.Count - 1; i >= 0; i--)
        {
            var v = _railVisuals[i];
            if (v.Surface == null || v.Rect == null)   // Unity ==: destroyed on menu close
            {
                _railVisuals.RemoveAt(i);
                continue;
            }
            bool hover = IsPointerOver(v.Rect, v.Canvas);
            v.Scale = Mathf.Lerp(v.Scale, hover ? 1.22f : 1f, 0.25f);
            float hoverProgress = Mathf.Clamp01((v.Scale - 1f) / 0.22f);
            var scale = new Vector3(v.Scale, v.Scale, 1f);
            if (v.Star != null) v.Star.rectTransform.localScale = scale;
            if (v.Glow != null)
            {
                v.Glow.rectTransform.localScale = scale;
                float alpha = Mathf.Clamp01((0.18f + 0.42f * pulse) + hoverProgress * 0.3f);
                v.Glow.color = new Color(a.R, a.G, a.B, alpha);
            }
        }
    }

    // Canvas is resolved once at build (cached on RailVisual) — a per-frame
    // GetComponentInParent<Canvas>() parent-walk + Il2CPP interop call is the one
    // avoidable cost while the rail is on screen. worldCamera is read off the
    // cached canvas each call (cheap) so a canvas camera swap is still honoured.
    private static bool IsPointerOver(RectTransform rt, Canvas? canvas)
    {
        var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var p = Input.mousePosition;
        return RectTransformUtility.RectangleContainsScreenPoint(rt, new Vector2(p.x, p.y), cam);
    }

    // Clear the EventSystem selection after our button is clicked, so the game's
    // selected-item highlight (the dark box) doesn't linger on the Stellar cell.
    private static void ClearRailSelection()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es != null) es.SetSelectedGameObject(null);
    }
}
