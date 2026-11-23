using Stellar.Infrastructure.Theme;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// The visible resize-grip mark: two strokes that follow the window body's bottom-right corner (arcs on a rounded
// frame, brackets on a square one — geometry in ResizeGripGeometry). Visual only: the 16×16 transparent hit Image
// built by MakeResizable is untouched, so drag behaviour is exactly as before.
internal sealed partial class WindowBuilder
{
    // Baked once per body radius, process-wide (a handful of radii exist; HideAndDontSave → survives scene unloads).
    // Main-thread only, like every WindowBuilder path. In a nested holder, not a WindowBuilder static: WindowBuilder
    // keeps no Unity-typed static init (see ContentOpacityRules — tests touch it without a Unity runtime).
    private static class GripMarkCache
    {
        internal static readonly System.Collections.Generic.Dictionary<int, (Texture2D tex, Sprite sprite)> Marks = new();
    }

    // Adds the mark as a non-raycast child of the grip. It is anchored to the WINDOW corner (the grip sits 1 px in
    // from it), sized 1:1 to its texture so every texel is one UI px — crisp, never a big texture scaled down. It may
    // extend past the 16 px hit box on a very round body; only the hit Image takes input. A grip child of a root
    // child, so WindowSpec.ContentOpacity's per-root-child CanvasGroup fades it exactly like the old dots.
    private static void BuildGripMark(GameObject grip, int bodyRadius)
    {
        var sprite = GripMarkSprite(bodyRadius);
        var size = sprite.texture.width;
        var mark = UGuiPrimitives.NewChild("GripMark", grip.transform);
        var mrt = mark.GetComponent<RectTransform>();
        mrt.anchorMin = mrt.anchorMax = mrt.pivot = new Vector2(1f, 0f);
        mrt.sizeDelta = new Vector2(size, size);
        mrt.anchoredPosition = new Vector2(1f, -1f);   // undo the grip's (-1, 1) inset → texture origin = window corner
        var img = mark.AddComponent<Image>();
        img.sprite = sprite; img.type = Image.Type.Simple; img.color = new Color(1f, 1f, 1f, 0.5f);   // stroke tint: white 50 %, texture alpha = coverage
        img.raycastTarget = false;
    }

    private static Sprite GripMarkSprite(int bodyRadius)
    {
        var key = System.Math.Clamp(bodyRadius, 0, ResizeGripGeometry.MaxRadius);
        if (GripMarkCache.Marks.TryGetValue(key, out var e))
        {
            if (e.sprite != null && e.tex != null) return e.sprite;   // Unity == null catches a destroyed half
            if (e.sprite != null) Object.Destroy(e.sprite);
            if (e.tex != null) Object.Destroy(e.tex);
        }
        var tex = BakeGripMark(key);
        var sprite = Sprite.Create(tex, new UnityEngine.Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f),
            pixelsPerUnit: 100f, extrude: 0, meshType: SpriteMeshType.FullRect);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        GripMarkCache.Marks[key] = (tex, sprite);
        return sprite;
    }

    // White texture whose alpha is the mark's coverage. No mip chain + bilinear: drawn 1:1, mips would only soften it.
    // Texel (x, y) centre → corner space: u = distance from the right edge, v = from the bottom (Unity y=0 is bottom).
    private static Texture2D BakeGripMark(int bodyRadius)
    {
        var n = ResizeGripGeometry.TextureSize(bodyRadius);
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, mipChain: false)
        {
            hideFlags = HideFlags.HideAndDontSave,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        var px = new Color[n * n];
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
            px[y * n + x] = new Color(1f, 1f, 1f, ResizeGripGeometry.Coverage(bodyRadius, n - (x + 0.5f), y + 0.5f));
        tex.SetPixels(px);
        tex.Apply(updateMipmaps: false);
        return tex;
    }
}
