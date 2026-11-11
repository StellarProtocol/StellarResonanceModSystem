using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The wrapper half of <see cref="PandaHudAdapter"/>: insert / re-check / remove the framework's own RectTransform
/// around an allowlist element. Recipe live-verified 2026-11-11 (Native-UI-Layout-Positioning.md "Wrapper spike"):
/// a full-stretch RectTransform (anchors 0..1, offsets 0, scale 1, parent's layer) inserted between a node and its
/// parent moves it by exactly 0 px; the offset is <c>wrapper.anchoredPosition</c>, the hide is
/// <c>wrapper.localScale = 0</c> (element stays active — skills by key still fire, HP/cooldowns keep updating).
/// We NEVER SetActive, translate or destroy a game node.
/// </summary>
internal sealed partial class PandaHudAdapter
{
    public bool IsWrapIntact(NativeUiHandle handle)
        => _cache.TryGetValue(handle.AllowlistPath, out var e) && IsWrapIntact(e);

    // Outer: the element still sits under our wrapper. Inner: our wrapper still sits under the element AND is its ONLY
    // child — the game instantiates content into some nodes AFTER we wrap (class gauge: the class-resource prefab
    // battle_res_uibinder_* lands in group_energy as a SIBLING of our wrapper, live 2026-11-11), and that late child
    // must be adopted or it stays at the game default. Polled at 10 Hz per customized element, so it uses
    // Transform.IsChildOf + childCount (value results) rather than reading .parent, which would allocate a fresh
    // managed proxy per call (Plugin-Performance-Rules §2). A destroyed wrapper/element fails the Unity-null checks first.
    private static bool IsWrapIntact(ResolvedEntry e)
    {
        var rt = e.RectTransform;
        var w = e.Wrapper;
        if (rt == null || w == null) return false;
        return e.Mode == NativeUiWrapMode.Inner ? rt.childCount == 1 && w.IsChildOf(rt) : rt.IsChildOf(w) && TwinsUnder(e, w);
    }

    public void Apply(NativeUiHandle handle, float offsetX, float offsetY, bool visible)
    {
        if (!_cache.TryGetValue(handle.AllowlistPath, out var e) || e.RectTransform == null) return;
        if (!IsWrapIntact(e) && !TryAdoptStrays(e))
        {
            DiscardWrapper(e);
            if (!CreateWrapper(e)) return;
        }
        var w = e.Wrapper!;
        // Keep the wrapper's pivot = its parent's (see Stretch). Two value reads on cached nodes, no proxy allocation;
        // a stretched rect with sizeDelta 0 doesn't move when its pivot changes, so re-syncing is invisible.
        var pp = e.WrapParent;
        if (pp != null && w.pivot != pp.pivot) w.pivot = pp.pivot;
        // Write only on change — our node is ours alone, so a value we set stays set (no re-assert needed).
        var pos = new Vector2(offsetX, offsetY);
        if (w.anchoredPosition != pos) w.anchoredPosition = pos;
        var scale = visible ? Vector3.one : Vector3.zero;
        if (w.localScale != scale) w.localScale = scale;
        if (e.BlockInputWhenHidden) SetInputBlocked(e, w, !visible);
    }

    // Inner wrapper still a direct child of the element, but the game added children beside it: move them INTO the
    // wrapper (after the ones it already holds, in their sibling order) so the offset/hide applies to them too, and keep
    // the wrapper first. In-place, so no destroy/re-create of our node. Allocates only on this (rare) path.
    private bool TryAdoptStrays(ResolvedEntry e)
    {
        var rt = e.RectTransform;
        var w = e.Wrapper;
        if (e.Mode != NativeUiWrapMode.Inner || rt == null || w == null) return false;
        var wp = w.parent;
        if (wp == null || wp.Pointer != rt.Pointer) return false;
        var n = rt.childCount;
        var strays = new System.Collections.Generic.List<Transform>(n);
        for (var i = 0; i < n; i++)
        {
            var c = rt.GetChild(i);
            if (c.Pointer != w.Pointer) strays.Add(c);
        }
        foreach (var c in strays) c.SetParent(w, false);
        w.SetAsFirstSibling();
        _log.Info($"[NativeUi] '{e.Id}' adopted {strays.Count} late child node(s) into its wrapper");
        return true;
    }

    public void Unwrap(NativeUiHandle handle)
    {
        if (_cache.TryGetValue(handle.AllowlistPath, out var e)) DiscardWrapper(e);
    }

    // Insert a fresh wrapper. Outer: wrapper takes the element's place (same parent, same sibling index), element
    // moves under it. Inner (view roots + LayoutGroup children): wrapper becomes the element's FIRST child and adopts
    // all its children in order — a view root is detached/re-parented to UILayerMain by the game on every show and
    // its own anchoredPosition rewritten (chat: (0,-5000) hidden / (0,0) shown), but its children stay ours.
    private bool CreateWrapper(ResolvedEntry e)
    {
        var rt = e.RectTransform!;
        var space = e.Mode == NativeUiWrapMode.Inner ? rt.transform : rt.parent;
        if (space == null) return false;   // outer needs a parent (a detached element is re-tried next liveness pass)
        // Inner: the wrapper's parent is the element itself. Outer: the element's parent — a non-RectTransform parent
        // (none on the allowlist) has no pivot to match, so it keeps pivot .5 and no re-sync.
        var parentRt = e.Mode == NativeUiWrapMode.Inner ? rt : space.TryCast<RectTransform>();
        var go = new GameObject(WrapperPath.Prefix + e.Id) { layer = space.gameObject.layer };
        var w = go.AddComponent<RectTransform>();
        // Children are re-parented with worldPositionStays=false only AFTER Stretch has set the pivot: with the
        // wrapper's rect AND local origin identical to the parent's, both their anchoredPosition and localPosition
        // mean the same thing under the wrapper as before → 0 px move at wrap time.
        if (e.Mode == NativeUiWrapMode.Inner)
        {
            var n = rt.childCount;
            var kids = new Transform[n];
            for (var i = 0; i < n; i++) kids[i] = rt.GetChild(i);
            w.SetParent(rt, false);
            Stretch(w, parentRt);
            for (var i = 0; i < n; i++) kids[i].SetParent(w, false);
            w.SetAsFirstSibling();
        }
        else
        {
            var idx = rt.GetSiblingIndex();
            w.SetParent(space, false);
            Stretch(w, parentRt);
            w.SetSiblingIndex(idx);
            rt.SetParent(w, false);
            AdoptTwins(e, w, space, idx);   // PandaHudAdapter.Twins.cs — the location bar's recycle twin
        }
        e.Wrapper = w;
        RegisterWrapper(w);   // PandaHudAdapter.PathWalk.cs: nested elements' path walks look through it
        e.WrapParent = parentRt;
        e.WrapperGroup = null;
        _log.Info($"[NativeUi] wrapped '{e.Id}' ({e.Mode})");
        WarnIfLaidOut(e, space);
        return true;
    }

    // A LayoutGroup on the wrapper's parent space would lay OUR wrapper out (overwriting its offset), and an inner
    // wrapper under a LayoutGroup element freezes the original children's layout. None is expected on the current
    // allowlist (class gauge is inner precisely to dodge its parent's group); log it so a new entry is caught in game.
    private void WarnIfLaidOut(ResolvedEntry e, Transform space)
    {
        if (space.GetComponent<UnityEngine.UI.LayoutGroup>() != null)
            _log.Warning($"[NativeUi] '{e.Id}' wrapper parent has a LayoutGroup — the offset may be overridden by layout");
    }

    // Full-stretch over the parent, pivot = the PARENT's pivot. The game writes `node.localPosition = v` on some
    // wrapped nodes (group_sceneline on every line switch, mainui_left_home_comp.lua), and localPosition is relative
    // to the parent's PIVOT: with a pivot-.5 wrapper under a pivot-(0,1)-ish node_upper_left those writes landed
    // half a parent off — the location bar jumped +(1280,720) px at 2560x1440 (live 2026-11-11). Matching pivots makes
    // the wrapper's local origin coincide with the parent's, so localPosition writes land exactly as unwrapped.
    // The user offset stays pivot-independent: for stretch anchors offsetMin = anchoredPosition − sizeDelta·pivot,
    // offsetMax = anchoredPosition + sizeDelta·(1−pivot), and sizeDelta is 0 → the rect is the parent's rect shifted
    // by anchoredPosition whatever the pivot, so stored .ox/.oy offsets mean the same px as before (no migration).
    private static void Stretch(RectTransform w, RectTransform? parent)
    {
        w.anchorMin = Vector2.zero;
        w.anchorMax = Vector2.one;
        w.pivot = parent != null ? parent.pivot : new Vector2(0.5f, 0.5f);
        w.offsetMin = Vector2.zero;
        w.offsetMax = Vector2.zero;
        w.localScale = Vector3.one;
        w.localRotation = Quaternion.identity;
    }

    // Remove our wrapper (if any), giving the game back exactly what it had: outer → the element returns to the
    // wrapper's parent at the wrapper's sibling index; inner → the wrapper's children return to the root at the
    // wrapper's index, in order. The wrapper is destroyed only once EMPTY — never take a game node down with it.
    private void DiscardWrapper(ResolvedEntry e)
    {
        var w = e.Wrapper;
        e.Wrapper = null;
        e.WrapperGroup = null;
        e.WrapParent = null;
        if (w == null) return;   // never wrapped, or it died with a rebuilt prefab
        var rt = e.RectTransform;
        if (e.Mode == NativeUiWrapMode.Inner)
        {
            if (rt != null && w.parent != null && w.parent.Pointer == rt.Pointer)
            {
                var idx = w.GetSiblingIndex();
                var n = w.childCount;
                var kids = new Transform[n];
                for (var i = 0; i < n; i++) kids[i] = w.GetChild(i);
                for (var i = 0; i < n; i++) { kids[i].SetParent(rt, false); kids[i].SetSiblingIndex(idx + i); }
            }
        }
        else if (e.Mode == NativeUiWrapMode.Outer && w.parent != null)
        {
            // Every child, not just the element: twins (PandaHudAdapter.Twins.cs) share the wrapper. In wrapper order,
            // starting at the wrapper's index — exactly the sibling order they had before we wrapped them. An outer
            // wrapper only ever holds game nodes WE put there, so this also returns a twin whose element the game
            // re-parented elsewhere (otherwise the twin would be stranded in a wrapper we then re-create beside it).
            var space = w.parent;
            var idx = w.GetSiblingIndex();
            var n = w.childCount;
            var kids = new Transform[n];
            for (var i = 0; i < n; i++) kids[i] = w.GetChild(i);
            for (var i = 0; i < n; i++) { kids[i].SetParent(space, false); kids[i].SetSiblingIndex(idx + i); }
        }
        // Unregister only once destroyed: a wrapper left holding game nodes must stay walk-through for path lookups.
        if (w.childCount == 0) { UnregisterWrapper(w); Object.Destroy(w.gameObject); }
        else _log.Warning($"[NativeUi] '{e.Id}' wrapper still holds {w.childCount} node(s); left in place (never destroying game UI)");
    }

    // Chat only: while hidden (scale 0) the input must not take focus — Enter would otherwise open an invisible chat
    // box. A CanvasGroup on OUR wrapper (never the game's ZUIComp CanvasGroup) with interactable/blocksRaycasts
    // off makes every Selectable below report !IsInteractable(), which InputField/TMP_InputField.ActivateInputField
    // checks. alpha stays 1 (the scale does the hiding), so it composes with the game's idle-fade CanvasGroup.
    private static void SetInputBlocked(ResolvedEntry e, RectTransform w, bool blocked)
    {
        if (e.WrapperGroup == null)
        {
            if (!blocked) return;
            e.WrapperGroup = w.gameObject.AddComponent<CanvasGroup>();
        }
        var g = e.WrapperGroup;
        if (g.interactable == blocked) g.interactable = !blocked;
        if (g.blocksRaycasts == blocked) g.blocksRaycasts = !blocked;
    }
}
