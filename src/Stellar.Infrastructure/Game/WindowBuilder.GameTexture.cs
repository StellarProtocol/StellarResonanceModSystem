using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Synchronous GameTextureElement binding + builder. Moved from WindowBuilder.DragSlot.cs and converted from an
// async external ticker (RegisterGameTexture, per-Unity-frame) to a WindowToken binding that runs inside
// WindowToken.Apply() — in the same pass as VirtualListBinding.Apply(). This eliminates the one-frame race where
// VirtualListBinding updated _winFirst and repositioned slots in one tick while the external ticker updated the
// icon Funcs in a different tick: for one Unity frame the slot was at its new position but still showed the
// PREVIOUS item's icon (or vice versa), producing a visible wrong-icon blink during scroll.
internal sealed partial class WindowBuilder
{
    internal sealed class GameTextureBinding
    {
        public RawImage Raw = null!;
        public Func<object?> Texture = null!;
        public Func<UvRect>? Uv;
        public float Width, Height;
        private object? _lastTex;
        private UvRect _lastUv;
        private bool _init;

        public void Apply()
        {
            if (Raw == null || !Raw.gameObject.activeInHierarchy) return;
            var tex = Texture();
            var uv = Uv != null ? Uv() : new UvRect(0f, 0f, 1f, 1f);
            var texChanged = !ReferenceEquals(tex, _lastTex);
            var uvChanged  = _init && !uv.Equals(_lastUv);
            if (!texChanged && !uvChanged) return;
            _lastTex = tex; _lastUv = uv; _init = true;
            var t = tex as Texture;
            Raw.texture = t;
            Raw.enabled  = t != null;
            if (t == null) return;
            Raw.uvRect = new Rect(uv.X, uv.Y, uv.W, uv.H);
            // Letterbox: fit the UV sub-rect's pixel footprint into the declared box, preserving aspect.
            float srcW = uv.W * t.width, srcH = uv.H * t.height;
            Raw.rectTransform.sizeDelta = AspectFit(Width, Height, srcW, srcH);
        }

        private static Vector2 AspectFit(float boxW, float boxH, float srcW, float srcH)
        {
            if (srcW <= 0f || srcH <= 0f) return new Vector2(boxW, boxH);
            float scale = Mathf.Min(boxW / srcW, boxH / srcH);
            return new Vector2(srcW * scale, srcH * scale);
        }
    }

    // Game-asset icon box. Replaces the async RegisterGameTexture ticker with a WindowToken binding so the icon
    // update is atomic with VirtualList slot repositioning (same Apply() pass → no wrong-icon blink on scroll).
    private void BuildGameTexture(GameTextureElement gt, Transform parent, WindowToken token)
    {
        var box = UGuiPrimitives.NewChild("GameTexture", parent);
        var le = box.AddComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = gt.Width;
        le.minHeight = le.preferredHeight = gt.Height;
        le.flexibleWidth = 0f;
        // RawImage on a centred child so the letterbox can resize it inside the fixed layout box without
        // stretching the box itself (a RawImage on the box node would squeeze the icon to the box shape).
        var go = UGuiPrimitives.NewChild("Icon", box.transform);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(gt.Width, gt.Height);
        var raw = go.AddComponent<RawImage>();
        raw.raycastTarget = false;
        raw.enabled = false;   // hidden until the first non-null texture arrives
        token.GameTextures.Add(new GameTextureBinding
            { Raw = raw, Texture = gt.Texture, Uv = gt.Uv, Width = gt.Width, Height = gt.Height });
    }
}
