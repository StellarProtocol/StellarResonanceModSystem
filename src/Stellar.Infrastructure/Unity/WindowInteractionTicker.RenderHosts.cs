using System;
using UnityEngine;
using UnityEngine.UI;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Unity;

// Live render-texture hosts (3D portrait) and game-asset icon hosts (profession crests / imagine icons).
// Split out of WindowInteractionTicker to keep that file under the LoC gate.
public sealed partial class WindowInteractionTicker
{
    // Live render-texture hosts (e.g. the inspector 3D portrait): each frame, pull the boxed Texture and bind it
    // onto the RawImage. Optional Drag (orbit) / Zoom (scroll) / Pan (shift+drag) callbacks make it interactive.
    internal readonly System.Collections.Generic.List<(RawImage Img, Func<object?> Texture, Action<float, float>? Drag, Action<float>? Zoom, Action<float, float>? Pan, Action<int, int>? Resize)> RenderHosts = new();
    // Game-asset icon hosts (GameTextureElement: profession crests / imagine icons): each frame, pull the boxed
    // texture and bind it onto the RawImage (hidden while null — async loads land late), plus the optional
    // dynamic UV sub-rect for atlas sprites. The non-interactive sibling of RenderHosts. `Bound` caches the
    // last managed handle so the per-frame diff never reads the interop texture getter back.
    internal struct IconHost
    {
        public RawImage Img; public Func<object?> Texture; public Func<UvRect>? Uv; public Texture? Bound;
        // Letterbox target: the RawImage's centred RectTransform is resized to the true texel aspect
        // (UV sub-rect × texture size) within the BoxW×BoxH layout box on bind, so sprites never
        // stretch to the box shape. Manual math, not AspectRatioFitter (IL2CPP stripping risk).
        public float BoxW, BoxH;
    }
    internal readonly System.Collections.Generic.List<IconHost> IconHosts = new();
    private int _activeRenderHost = -1;

    // Scroll over a render-host box → zoom callback (e.g. the portrait camera). Scoped to the box rect so it
    // doesn't fight the attribute-list scroll wheel (the list is a separate region).
    private void TickRenderHostZoom()
    {
        var scroll = Input.mouseScrollDelta.y;
        if (scroll == 0f) return;
        var mp = Input.mousePosition;
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, _, _, zoom, _, _) = RenderHosts[i];
            if (img == null || zoom == null || !img.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(img.rectTransform, mp, null)) continue;
            try { zoom(scroll); } catch { }
            return;
        }
    }

    // Bind each render-texture host's current texture onto its RawImage (cheap; the texture may appear late).
    private void TickRenderHosts()
    {
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, fn, _, _, _, resize) = RenderHosts[i];
            if (img == null) continue;
            try
            {
                // Report the box's current pixel size FIRST so the preview resizes its RT before we bind it —
                // binding the old RT then destroying it (in resize) showed a destroyed texture for a frame (the
                // white resize flicker). The preview fills the pane top-to-bottom with no letterbox or stretch.
                if (resize != null)
                {
                    var r = img.rectTransform.rect;
                    if (r.width >= 1f && r.height >= 1f) resize((int)r.width, (int)r.height);
                }
                // Hide the RawImage while its texture is null (the model is still loading) — an enabled
                // RawImage with no texture draws uGUI's default SOLID WHITE, which flashed the pane white on
                // every open until the model landed. With it hidden, the dark RenderHostBg backdrop shows
                // instead. Same guard the icon-host path (TickIconHosts) already applies.
                var tex = fn() as Texture;
                img.texture = tex;
                if (img.enabled != (tex != null)) img.enabled = tex != null;
            }
            catch { }
        }
    }

    // Bind each icon host's current game-asset texture onto its RawImage (cheap; async loads land late).
    // The image stays hidden while the texture is null so no white placeholder box flashes, and the optional
    // UV func re-points the RawImage at the right atlas cell (value-diffed — assignment dirties the canvas).
    // Hosts inside a hidden window/tab are skipped entirely; a late-loaded icon binds on its first active frame.
    private void TickIconHosts()
    {
        for (var i = 0; i < IconHosts.Count; i++)
        {
            var h = IconHosts[i];
            if (h.Img == null || !h.Img.gameObject.activeInHierarchy) continue;
            try
            {
                var tex = h.Texture() as Texture;
                // Resolve the UV BEFORE binding/enabling so a throwing Uv func fails closed (icon stays
                // hidden) instead of showing the whole atlas sheet at the default 0,0,1,1 rect.
                var uvChanged = false;
                if (tex != null && h.Uv != null)
                {
                    var r = h.Uv();
                    var rect = new Rect(r.X, r.Y, r.W, r.H);
                    if (h.Img.uvRect != rect) { h.Img.uvRect = rect; uvChanged = true; }
                }
                if (!ReferenceEquals(h.Bound, tex))
                {
                    h.Img.texture = tex;
                    h.Img.enabled = tex != null;
                    h.Bound = tex;
                    IconHosts[i] = h;
                    uvChanged = tex != null;   // (re)bound — refresh the letterbox aspect too
                }
                if (uvChanged && tex != null) UpdateIconAspect(h, tex);
            }
            catch { if (_throwLogged++ == 0) UnityEngine.Debug.LogWarning("[Window] icon-host tick threw (rate-limited)"); }
        }
    }

    // True displayed aspect = UV sub-rect × texture size (atlas cells are rarely the box shape).
    // Only called on bind / UV change — never per steady-state frame (tex.width crosses interop).
    private static void UpdateIconAspect(in IconHost h, Texture tex)
    {
        if (h.BoxW <= 0f || h.BoxH <= 0f) return;
        var uv = h.Img.uvRect;
        float tw = uv.width * tex.width, th = uv.height * tex.height;
        if (tw <= 0f || th <= 0f) return;
        var texAspect = tw / th;
        float w, ht;
        if (texAspect > h.BoxW / h.BoxH) { w = h.BoxW; ht = h.BoxW / texAspect; }
        else                             { ht = h.BoxH; w = h.BoxH * texAspect; }
        h.Img.rectTransform.sizeDelta = new Vector2(w, ht);
    }

    // Drag over a render-host box → orbit its camera; with Shift held → pan instead (move the camera).
    private void TickRenderHostDrag()
    {
        if (_activeRenderHost >= RenderHosts.Count) { _activeRenderHost = -1; return; }
        var host = RenderHosts[_activeRenderHost];
        var m = (Vector2)Input.mousePosition;
        var d = m - _lastMouse;
        _lastMouse = m;
        var shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        try
        {
            if (shift && host.Pan != null) host.Pan(d.x, d.y);
            else host.Drag?.Invoke(d.x, d.y);
        }
        catch { }
    }

    private int HitRenderHost(Vector3 mp)
    {
        for (var i = 0; i < RenderHosts.Count; i++)
        {
            var (img, _, drag, _, pan, _) = RenderHosts[i];
            if (img == null || (drag == null && pan == null) || !img.gameObject.activeInHierarchy) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(img.rectTransform, mp, null)) return i;
        }
        return -1;
    }
}
