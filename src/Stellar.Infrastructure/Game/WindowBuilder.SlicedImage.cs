using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// SlicedImageElement (framework 2.34.0): plugin frame art as a horizontal 3-slice — caps at native width, the middle
// stretched between them, fixed height. Three anchor-positioned RawImages under one layout slot (no LayoutGroup, no
// per-refresh work): a width change (window resize) moves only the anchors' resolved rects.
//
// Why three PNGs instead of one PNG + Image.Type.Sliced borders: the art ships as three pieces already, and a RawImage
// per piece needs no Sprite (no border metadata, no pixelsPerUnit coupling to the canvas). Pieces load through LoadIcon
// with forceExact (no mip chain + bilinear) so at UI scale 1 each texel lands on one pixel; LoadIcon's per-token byte[]
// dedup + IconTextures ownership handle sharing and teardown. A piece whose PNG height differs from Height is scaled
// uniformly (a 1× art set can be drawn at Height = 2× its pixels and stays in proportion).
internal sealed partial class WindowBuilder
{
    private void BuildSlicedImage(SlicedImageElement si, Transform parent, WindowToken token)
    {
        var go = UGuiPrimitives.NewChild("SlicedImage", parent);
        var left = LoadIcon(si.Left(), token, forceExact: true);
        var mid = LoadIcon(si.Middle(), token, forceExact: true);
        var right = LoadIcon(si.Right(), token, forceExact: true);
        float h = si.Height > 0 ? si.Height : left != null ? left.height : 0f;
        float lw = NativeWidth(left, h), mw = NativeWidth(mid, h), rw = NativeWidth(right, h);

        var le = go.AddComponent<LayoutElement>();
        le.minHeight = le.preferredHeight = h; le.flexibleHeight = 0f;
        le.minWidth = lw + rw; le.preferredWidth = lw + mw + rw; le.flexibleWidth = 1f;

        Piece(go.transform, "Left", left, new Vector2(0f, 0f), new Vector2(0f, lw));
        Piece(go.transform, "Middle", mid, new Vector2(0f, 1f), new Vector2(lw, -rw));
        Piece(go.transform, "Right", right, new Vector2(1f, 1f), new Vector2(-rw, 0f));
    }

    private static float NativeWidth(Texture2D? t, float h)
        => t == null || t.height <= 0 ? 0f : t.height == (int)h ? t.width : t.width * (h / t.height);

    // One piece: x anchors (min, max) at full height, x offsets (min, max) in px from those anchors.
    private static void Piece(Transform parent, string name, Texture2D? tex, Vector2 anchorX, Vector2 offsetX)
    {
        if (tex == null) return;   // a missing piece is just not drawn; the slot keeps its size
        var go = UGuiPrimitives.NewChild(name, parent);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(anchorX.x, 0f); rt.anchorMax = new Vector2(anchorX.y, 1f);
        rt.offsetMin = new Vector2(offsetX.x, 0f); rt.offsetMax = new Vector2(offsetX.y, 0f);
        var raw = go.AddComponent<RawImage>();
        raw.texture = tex; raw.raycastTarget = false;
    }
}
