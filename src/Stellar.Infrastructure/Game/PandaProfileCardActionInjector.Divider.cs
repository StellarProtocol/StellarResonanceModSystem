using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The thin vertical DIVIDER line between action buttons. The native items
/// (<c>ui/prefabs/idcard/idcard_item_tpl_pc</c>, built by <c>idcard_view.lua</c>) each carry their divider
/// on their right edge as a STATIC child of the template prefab — Lua only sets <c>img_card</c> /
/// <c>lab_card</c> and binds <c>btn_idcard</c>, nothing touches a divider — so our from-scratch buttons had
/// none. We locate that node on a native sibling item and clone it onto each injected button at the same
/// relative position. Not found → no divider (the old look), never a throw.
/// </summary>
internal sealed partial class PandaProfileCardActionInjector
{
    private const string DividerName = "stellar_divider";
    // Geometry fallback for "is this a vertical hairline": at most this wide, and at least this many times
    // taller than it is wide (rect.size, i.e. local units before scale).
    private const float DividerMaxWidth = 6f;
    private const float DividerMinAspect = 3f;

    private bool _dividerFailLogged;

    // The native divider to clone: its live GameObject plus its geometry re-expressed relative to the item
    // root (see DividerGeometry). DirectChild = it hangs straight off the item root, so its own anchors /
    // scale carry over verbatim; otherwise Geometry is a fixed bottom-left-anchored rect in item-root space.
    private readonly struct NativeDivider
    {
        public readonly GameObject? Source;
        public readonly NodeStyle Geometry;
        public readonly bool DirectChild;
        public bool Found => Source != null;

        public NativeDivider(GameObject source, NodeStyle geometry, bool directChild)
        {
            Source = source;
            Geometry = geometry;
            DirectChild = directChild;
        }
    }

    // Find the divider on the native ITEM root (btn_idcard's parent — the cell under layout_interactive).
    // Logs the item's hierarchy once (gated) so the real node name can be recorded in the KB.
    private NativeDivider CaptureDivider(Transform sibling, TMP_Text? label)
    {
        var item = sibling.parent;
        if (item == null) return default;
        try
        {
            var g = FindDividerGraphic(item, sibling, label);
            var result = g != null && g.rectTransform != null
                ? new NativeDivider(g.gameObject, DividerGeometry(item, g.rectTransform), g.transform.parent == item)
                : default;
            DiagDividerHierarchy(item, g);
            return result;
        }
        catch (Exception ex)
        {
            WarnDividerOnce($"divider capture threw: {ex.GetType().Name}: {ex.Message}");
            return default;
        }
    }

    // Pick the divider Graphic: a name hint ("line"/"div"/"sep") wins; otherwise the first thin vertical
    // Graphic. Never the item root, the button's own graphic/target graphic, the icon (img_card) or the
    // label (lab_card / any TMP text). Null when nothing qualifies.
    private static Graphic? FindDividerGraphic(Transform item, Transform sibling, TMP_Text? label)
    {
        var btn = sibling.GetComponent<UnityEngine.UI.Button>();
        var btnTarget = btn != null ? btn.targetGraphic : null;
        var graphics = item.GetComponentsInChildren<Graphic>(includeInactive: true);
        Graphic? byGeometry = null;
        for (var i = 0; i < graphics.Length; i++)
        {
            var g = graphics[i];
            if (!IsDividerCandidate(g, item, sibling, label, btnTarget)) continue;
            if (NameSuggestsDivider(g.gameObject.name)) return g;
            if (byGeometry == null && LooksLikeVerticalLine(g)) byGeometry = g;
        }
        return byGeometry;
    }

    private static bool IsDividerCandidate(Graphic? g, Transform item, Transform sibling, TMP_Text? label, Graphic? btnTarget)
    {
        if (g == null || g.transform == item || g.transform == sibling) return false;
        if (btnTarget != null && g.gameObject == btnTarget.gameObject) return false;
        if (label != null && g.gameObject == label.gameObject) return false;
        if (g.TryCast<TMP_Text>() != null) return false;   // IL2CPP wrapper: `is TMP_Text` is always false
        var name = g.gameObject.name;
        return !string.Equals(name, "img_card", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, "lab_card", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NameSuggestsDivider(string name) =>
        name.IndexOf("line", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("div", StringComparison.OrdinalIgnoreCase) >= 0
        || name.IndexOf("sep", StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool LooksLikeVerticalLine(Graphic g)
    {
        var rt = g.rectTransform;
        if (rt == null) return false;
        var s = rt.rect.size;
        return s.x > 0f && s.x <= DividerMaxWidth && s.y >= DividerMinAspect * s.x;
    }

    // The divider's rect relative to the item root. A direct child keeps its own anchors/pivot/position
    // (so a right-edge-anchored, height-stretched line stays that way on our button). A nested one has
    // anchors relative to an intermediate parent, so re-express its live rect in item-root local space as
    // a bottom-left-anchored fixed rect — valid because our button is sized to the same cell.
    private static NodeStyle DividerGeometry(Transform item, RectTransform rt)
    {
        var color = rt.GetComponent<Graphic>() is { } g ? g.color : Color.white;
        if (rt.parent == item) return new NodeStyle(rt, color);

        var itemRt = item.TryCast<RectTransform>();
        if (itemRt == null) return new NodeStyle(rt, color);
        var r = rt.rect;
        Vector2 min = item.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMin, r.yMin, 0f)));
        Vector2 max = item.InverseTransformPoint(rt.TransformPoint(new Vector3(r.xMax, r.yMax, 0f)));
        return new NodeStyle(Vector2.zero, Vector2.zero, min - itemRt.rect.min, max - min, color);
    }

    // Clone the captured native divider under our button and place it at the native relative position.
    // Guarded against double-add (a re-used button keeps its divider). Stripped to visuals only, with every
    // Graphic non-raycastable, so it can never interfere with the manual hit-test.
    private void AddDivider(Transform parent, NativeDivider divider)
    {
        if (!divider.Found || parent.Find(DividerName) != null) return;
        try
        {
            var go = UnityEngine.Object.Instantiate(divider.Source!, parent, false)?.TryCast<GameObject>();
            if (go == null) return;
            go.name = DividerName;
            StripToVisuals(go);
            go.SetActive(true);   // the native node may be hidden on the sampled item; ours must show
            var rt = go.transform.TryCast<RectTransform>();
            if (rt == null) return;
            divider.Geometry.ApplyTo(rt);
            // Nested geometry already bakes intermediate-parent + own scale into its item-space size.
            if (!divider.DirectChild) rt.localScale = Vector3.one;
        }
        catch (Exception ex)
        {
            WarnDividerOnce($"divider clone threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Keep Transform/RectTransform, CanvasRenderer and Graphics (raycastTarget off); destroy everything else
    // a prefab node might carry (buttons, layout elements, animators, Panda behaviours) on the clone.
    private static void StripToVisuals(GameObject go)
    {
        var comps = go.GetComponentsInChildren<Component>(includeInactive: true);
        for (var i = 0; i < comps.Length; i++)
        {
            var c = comps[i];
            if (c == null || c.TryCast<Transform>() != null || c.TryCast<CanvasRenderer>() != null) continue;
            var g = c.TryCast<Graphic>();
            if (g != null) { g.raycastTarget = false; continue; }
            UnityEngine.Object.Destroy(c);
        }
    }

    private void WarnDividerOnce(string msg)
    {
        if (_dividerFailLogged) return;
        _dividerFailLogged = true;
        _log.Warning($"[IdCard] {msg} — action buttons built without a divider");
    }
}
