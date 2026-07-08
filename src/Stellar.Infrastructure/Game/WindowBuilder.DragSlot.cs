using UnityEngine;
using UnityEngine.UI;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

// Builds DragSlotElement: the child cell + a (hidden) drop-target highlight overlay, registered with the
// WindowInteractionTicker so it can be dragged/dropped. The ticker drives the gesture (ghost follows cursor,
// hover highlight, drop → OnDrop). The drop callback is shared across all cells of one grid, so it is set once
// per build. Kept in a sibling partial so WindowBuilder.cs stays under the file-size gate.
internal sealed partial class WindowBuilder
{
    private static readonly System.Func<bool> AlwaysDraggable = () => true;

    private void BuildDragSlot(DragSlotElement ds, Transform parent, WindowToken token)
    {
        // Host is a 0-gap column wrapping the child + an ignore-layout highlight, so it adds no layout shift.
        var host = UGuiPrimitives.NewChild("DragSlot", parent);
        UGuiPrimitives.AddLayout(host, gap: 0f, columns: UGuiPrimitives.ColumnMode);
        var hostRt = host.GetComponent<RectTransform>();

        // Drop-target highlight: a faint cyan fill stretched over the cell, hidden until hovered while dragging.
        var hl = UGuiPrimitives.NewChild("DropHighlight", host.transform);
        hl.AddComponent<LayoutElement>().ignoreLayout = true;
        UGuiPrimitives.Stretch(hl);
        var hlImg = hl.AddComponent<Image>();
        hlImg.color = new Color(0.45f, 0.78f, 1f, 0.28f);
        hlImg.raycastTarget = false;
        hl.SetActive(false);

        BuildElement(ds.Child, host.transform, token);

        RegisterDragSlot?.Invoke(hostRt, ds.Key, ds.CanDrag ?? AlwaysDraggable,
            on => { if (hl != null) hl.SetActive(on); });
        SetDragSlotDrop?.Invoke(ds.OnDrop);
    }

    // Render-texture box (the inspector 3D portrait). The pane is fixed-width but FLEXIBLE height so it fills the
    // window's content height as the window grows; the child image is held at the texture's true aspect by an
    // AspectRatioFitter so resizing never stretches the character fat/thin (it letterboxes within the pane). The
    // ticker binds the texture + feeds the live aspect each frame (the texture/RT size is known only at runtime).
    private void BuildRenderHost(RenderTextureHostElement rh, Transform parent)
    {
        var go = UGuiPrimitives.NewChild("RenderHost", parent);
        var le = go.AddComponent<LayoutElement>();
        le.minWidth = le.preferredWidth = rh.Width;
        le.minHeight = rh.Height;
        le.flexibleHeight = 1f;
        // Opaque backdrop filling the whole pane (drawn first → behind the model). A full-alpha Image so the pane
        // is solid regardless of the window's frame-opacity setting — the live world no longer shows through
        // behind / around the model. Skipped for TransparentBackground panes (the RT clears to alpha 0 —
        // PortraitCmdRenderer.ClearRenderTarget — so only the rendered subject draws over the window chrome).
        if (!rh.TransparentBackground)
        {
            var bgGo = UGuiPrimitives.NewChild("RenderHostBg", go.transform);
            var bg = bgGo.AddComponent<Image>();
            bg.color = new Color(0.11f, 0.12f, 0.15f, 1f);
            bg.raycastTarget = false;
            var bgRt = bgGo.GetComponent<RectTransform>();
            bgRt.anchorMin = Vector2.zero; bgRt.anchorMax = Vector2.one;
            bgRt.offsetMin = Vector2.zero; bgRt.offsetMax = Vector2.zero;
        }
        // RawImage fills the whole pane (stretch). The render texture is sized to the pane each frame (via the
        // OnViewportResize callback → the preview resizes its RT + projection), so it fills with no letterbox or
        // stretch even as the window is resized.
        var imgGo = UGuiPrimitives.NewChild("RenderHostImg", go.transform);
        var raw = imgGo.AddComponent<RawImage>();
        raw.raycastTarget = false;
        var imgRt = imgGo.GetComponent<RectTransform>();
        imgRt.anchorMin = Vector2.zero; imgRt.anchorMax = Vector2.one;
        imgRt.offsetMin = Vector2.zero; imgRt.offsetMax = Vector2.zero;
        RegisterRenderHost?.Invoke(raw, rh.Texture, rh.OnDrag, rh.OnScroll, rh.OnPan, rh.OnViewportResize);
    }

}
