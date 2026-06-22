using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

/// <summary>Element construction for <see cref="PandaUGuiAdapter"/> (kept in a sibling partial).</summary>
internal sealed partial class PandaUGuiAdapter
{
    // Memoised live-template lookups: FindLiveByName searches the menu panel subtree
    // (GetComponentsInChildren scoped to the resolved parent), so we cache the result
    // per name. Unity's overloaded == nulls the entry once the game destroys it
    // (menu close) and the activeInHierarchy guard rejects pooled-inactive ghosts,
    // so a hit only stands while the template is genuinely live — one subtree scan
    // per menu-open instead of one per injection tick.
    private readonly Dictionary<string, Transform> _liveCache = new();

    // Turns a plugin/framework IconPng into a Texture2D for the rail button's
    // icon slot (StatIconAtlas discipline: HideAndDontSave + self-heal).
    private readonly PluginIconCache _iconCache = new();

    // Rail buttons we animate each frame: pulse the accent glow halo + apply a
    // manual hover wash (the game's InputSystem EventSystem doesn't dispatch
    // PointerEnter/Exit to our injected button, so uGUI ColorTint never fired —
    // we poll the pointer against the cell rect instead). Destroyed entries
    // (menu close) are pruned in TickGlow.
    private sealed class RailVisual
    {
        public RawImage? Glow;     // accent halo (pulsed + brightened on hover); may be null
        public RawImage? Star;     // crisp accent star (scaled on hover)
        public Image Surface = null!;       // transparent click surface (kept for liveness check)
        public RectTransform Rect = null!;  // cell rect (hover hit-test)
        public Canvas? Canvas;              // owning canvas, resolved once at build (hover hit-test camera)
        public float Scale = 1f;            // current hover scale (lerped)
    }
    private readonly System.Collections.Generic.List<RailVisual> _railVisuals = new();
    private RawImage? _pendingGlow;   // set by AddGlowingRailIcon during a build, read into the RailVisual
    private RawImage? _pendingStar;

    // Dedup for the transient "named rail template not live this tick" miss.
    // The injection service retries every tick while the menu is open, so the
    // miss self-corrects; without this guard it logged a Warning every 0.2 s on
    // a normal menu-open. Logged once (Info), reset on a successful build.
    private bool _railTemplateMissLogged;

    // IconKey → glyph for MenuButton. "icons are a glyph/text" per the v1 design;
    // unknown/absent keys just render the bare label. NOTE: whether the built-in
    // font (Liberation Sans under Wine) actually draws these symbol glyphs vs a
    // tofu box is UNVERIFIED and must be confirmed in-world — the Phase 9a hub fell
    // back to ASCII for exactly this reason. Only "gear" (⚙) ships in the demo.
    private static readonly Dictionary<string, string> IconGlyphs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gear"] = "⚙", ["settings"] = "⚙", ["info"] = "ℹ",
        ["star"] = "★", ["menu"] = "☰", ["plus"] = "＋",
        ["chart"] = "≡", ["wrench"] = "⚒",
    };
    private GameObject? BuildButton(NativeUiAnchor anchor, MenuButtonSpec spec)
    {
        if (!UGuiAnchorAllowlist.TryGet(anchor, out var e) || e.TemplateChildName == null)
        { _log.Warning($"[uGUI] anchor {anchor} has no template anchor"); return null; }

        // Locate a LIVE rail item for SIZE/POSITION context, but BUILD the button
        // from scratch: cloning the game item drags its data-binder (label stuck
        // on "Loading", non-interactive) and its graphics aren't Unity-raycastable.
        // A from-scratch Image+Button+Text is reliably clickable (proven in spike).
        // Use ONLY the named top-group template for geometry + placement. (An
        // earlier "any live *_btn*" fallback could grab a bottom-group button —
        // Support/Unstuck/Exit — and place Stellar overlapping them.) If the named
        // template is briefly pooled-inactive, build nothing this tick and retry.
        var parent = ResolveParent(anchor);
        var template = parent != null ? FindLiveByName(e.TemplateChildName, parent) : null;
        if (template == null || template.parent == null)
        {
            if (!_railTemplateMissLogged)
            {
                _log.Info($"[uGUI] no live rail item for {anchor} yet — retrying next tick");
                _railTemplateMissLogged = true;
            }
            return null;
        }
        _railTemplateMissLogged = false;

        var tmplRt = template.TryCast<RectTransform>();
        var size = tmplRt != null && tmplRt.sizeDelta.x > 1f ? tmplRt.sizeDelta : new Vector2(64f, 64f);
        var go = NewUiObject($"StellarBtn_{spec.Label}", template.parent, size);
        var rt = go.GetComponent<RectTransform>();
        if (tmplRt != null)
        {
            rt.anchorMin = tmplRt.anchorMin; rt.anchorMax = tmplRt.anchorMax; rt.pivot = tmplRt.pivot;
            rt.anchoredPosition = tmplRt.anchoredPosition;
        }
        _pendingGlow = null; _pendingStar = null;
        var hasPng = AddRailButtonContent(go, spec, size, template);
        AttachAndPlaceButton(go, spec, template, hasPng);
        return go;
    }

    private void AttachAndPlaceButton(GameObject go, MenuButtonSpec spec, Transform template, bool hasPng)
    {
        var rt = go.GetComponent<RectTransform>();
        var btn = go.AddComponent<Button>();
        // Hover is handled manually in TickGlow (the InputSystem EventSystem
        // doesn't dispatch PointerEnter/Exit to our injected button, so uGUI's
        // ColorTint never fires); None keeps the Button from any auto-tint.
        btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener((UnityAction)(() => { SafeInvoke(spec.OnClick); ClearRailSelection(); }));
        PlaceBelowRail(go, template.parent, template.name);
        _railVisuals.Add(new RailVisual { Glow = _pendingGlow, Star = _pendingStar, Surface = go.GetComponent<Image>(), Rect = rt, Canvas = rt.GetComponentInParent<Canvas>() });
        _log.Info($"[uGUI] built rail button '{spec.Label}' under '{template.parent.name}' for {spec.Anchor}"
                  + (hasPng ? " (png icon)" : ""));
    }

    // Native rail items are icon-on-top + label-below on a transparent cell (no
    // per-item background sprite), so we mirror that: a near-invisible raycast
    // surface, an icon in the top area, label beneath. A real PNG sprite
    // (IconPng) is preferred — font glyphs tofu on the rail font; glyph is the
    // fallback, bare label the last resort. Returns true if a PNG icon was used.
    private bool AddRailButtonContent(GameObject go, MenuButtonSpec spec, Vector2 size, Transform template)
    {
        // Fully-transparent click surface — a 0-alpha Image still raycasts
        // (raycastTarget on, alpha-hit-test off), same as the Phase 0 blocker.
        AddSolid(go, new Color(0f, 0f, 0f, 0f));
        var iconTex = _iconCache.Get(spec.IconPng);
        if (iconTex != null)
        {
            AddGlowingRailIcon(go.transform, iconTex, Mathf.Min(size.x, size.y) * 0.55f, 0.66f);
            AddRailLabel(go.transform, spec.Label, new Vector2(0f, 0f), new Vector2(1f, 0.34f), template);
            return true;
        }
        var glyph = Glyph(spec.IconKey);
        if (glyph != null)
        {
            AddTextRegion(go.transform, glyph, new Vector2(0f, 0.40f), new Vector2(1f, 1f), 26);
            AddRailLabel(go.transform, spec.Label, new Vector2(0f, 0f), new Vector2(1f, 0.42f), template);
        }
        else
        {
            AddRailLabel(go.transform, spec.Label, Vector2.zero, Vector2.one, template);
        }
        return false;
    }

    // Places the Stellar button one step below the LAST item of the rail's TOP
    // group (after Notice). The rail has two groups — the top list and a
    // bottom-pinned Support/Unstuck/Exit cluster — separated by a big gap; we
    // stop at that gap so Stellar never lands among the bottom group.
    private static void PlaceBelowRail(GameObject clone, Transform parent, string itemName)
    {
        var crt = clone.transform.TryCast<RectTransform>();
        if (crt == null) return;

        var ys = new List<float>();
        for (var i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c == clone.transform || c.name != itemName) continue;
            var r = c.TryCast<RectTransform>(); if (r == null) continue;
            ys.Add(r.anchoredPosition.y);
        }
        if (ys.Count == 0) return;

        ys.Sort();
        ys.Reverse();   // descending: top (largest y) → bottom (smallest y)
        var fallback = crt.sizeDelta.y > 0 ? crt.sizeDelta.y : 60f;
        var step = ys.Count >= 2 ? ys[0] - ys[1] : fallback;
        if (step <= 0f) step = fallback;

        var lastTopY = ys[0];
        for (var i = 1; i < ys.Count; i++)
        {
            if (lastTopY - ys[i] > step * 1.6f) break;   // big gap → bottom group; stop
            lastTopY = ys[i];
        }
        crt.anchoredPosition = new Vector2(crt.anchoredPosition.x, lastTopY - step);
    }

    // Finds the first active descendant of <paramref name="parent"/> with the given
    // name, excluding our own injected buttons. Scoped to the menu panel subtree so
    // it's far cheaper than the previous Resources.FindObjectsOfTypeAll scene-wide
    // scan. Result memoised in _liveCache; cached hit reused while still live.
    private Transform? FindLiveByName(string name, Transform parent)
    {
        if (_liveCache.TryGetValue(name, out var cached) && IsUsableTemplate(cached, name))
            return cached;

        foreach (var t in parent.GetComponentsInChildren<Transform>(includeInactive: false))
        {
            if (t == null || t.name != name) continue;
            if (!t.name.StartsWith("StellarBtn_"))
            {
                _liveCache[name] = t;
                return t;
            }
        }
        _liveCache.Remove(name);
        return null;
    }

    // A cached template is still good only if Unity hasn't destroyed it (== null)
    // and it's active in the live scene.
    private static bool IsUsableTemplate(Transform? t, string name)
        => t != null && t.name == name && t.gameObject.activeInHierarchy && t.gameObject.scene.IsValid();

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

    private GameObject BuildIndicator(Transform parent, IndicatorSpec spec)
    {
        var go = NewUiObject("StellarIndicator", parent, new Vector2(160f, 28f));
        AddSolid(go, new Color(0.08f, 0.11f, 0.15f, 0.85f));
        AddText(go.transform, spec.OnUpdate(), spec.Tint);
        StackUnder(go, parent);
        return go;
    }

    private GameObject BuildPanel(Transform parent, PanelSpec spec)
    {
        var go = NewUiObject("StellarPanel", parent, new Vector2(220f, 24f + spec.Children.Count * 22f));
        AddSolid(go, new Color(0.08f, 0.11f, 0.15f, 0.92f));
        var y = -12f;
        foreach (var w in spec.Children)
        {
            var row = AddText(go.transform, DescribeWidget(w), null);
            row.alignment = TextAnchor.MiddleLeft; // data rows read left-aligned, not centred
            var rt = row.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0f, y); rt.sizeDelta = new Vector2(-12f, 20f);
            y -= 22f;
        }
        StackUnder(go, parent);
        return go;
    }

    // Pin a from-scratch HUD element to the TOP-RIGHT corner of its anchor (the
    // HudTopRight parent spans the screen width, so a centre anchor lands it
    // mid-screen — observed in-world) and offset it below any from-scratch Stellar
    // siblings already there, so multiple Indicators/Panels stack down the right edge.
    private static void StackUnder(GameObject go, Transform parent)
    {
        var rt = go.GetComponent<RectTransform>();
        var index = 0;
        for (var i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            // Count only from-scratch HUD siblings (Indicator/Panel); rail buttons
            // ("StellarBtn_…") are positioned by PlaceBelowRail, not stacked here.
            if (c != go.transform && IsStackedSibling(c.name)) index++;
        }
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
        rt.anchoredPosition = new Vector2(-12f, -8f - index * (rt.sizeDelta.y + 6f));

        // If the anchor carries a LayoutGroup it would overwrite anchoredPosition
        // every frame; opt out so our explicit stacking holds. Harmless no-op when
        // the parent has no layout driver.
        var le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
        le.ignoreLayout = true;
    }

    private static bool IsStackedSibling(string name)
        => name.StartsWith("StellarIndicator") || name.StartsWith("StellarPanel");

    // Pre-built fixed-width bar strings indexed 0..10 so BarWidget refreshes don't
    // allocate a fresh char-array string each tick.
    private static readonly string[] BarFills = BuildBarFills();
    private static string[] BuildBarFills()
    {
        var a = new string[11];
        for (var i = 0; i <= 10; i++) a[i] = "[" + new string('#', i) + new string(' ', 10 - i) + "]";
        return a;
    }

    private static string DescribeWidget(PanelWidget w) => w switch
    {
        LabelWidget l    => l.Text,
        ValueRowWidget v => $"{v.Key}: {v.Value()}",
        BarWidget b      => BarFills[(int)(Math.Clamp(b.Fraction01(), 0f, 1f) * 10)],
        _                => "",
    };

    private void ApplyDynamic(ElementRef e, NativeUiElementSpec spec)
    {
        // Resolve the content Text components once and cache them on the ref; the
        // child set is fixed after build, so re-scanning each tick is pure waste.
        e.Texts ??= e.Go.GetComponentsInChildren<Text>(includeInactive: true);
        if (spec is IndicatorSpec ind)
        {
            if (e.Texts.Length > 0 && e.Texts[0] != null) e.Texts[0].text = ind.OnUpdate();
        }
        else if (spec is PanelSpec p)
        {
            for (var i = 0; i < e.Texts.Length && i < p.Children.Count; i++)
                if (e.Texts[i] != null) e.Texts[i].text = DescribeWidget(p.Children[i]);
        }
    }

    private void SafeInvoke(Action a)
    {
        try { a(); } catch (Exception ex) { _log.Error($"[uGUI] callback threw: {ex}"); }
    }

    private static GameObject NewUiObject(string name, Transform parent, Vector2 size)
    {
        var go = new GameObject(name);
        go.AddComponent<CanvasRenderer>();
        var rt = go.AddComponent<RectTransform>();
        go.transform.SetParent(parent, worldPositionStays: false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero; rt.sizeDelta = size; rt.localScale = Vector3.one;
        return go;
    }

    private static Image AddSolid(GameObject go, Color c) { var img = go.AddComponent<Image>(); img.color = c; return img; }

    // "icons are a glyph/text" (v1): the glyph for this IconKey, or null for none.
    private static string? Glyph(string? iconKey)
        => iconKey != null && IconGlyphs.TryGetValue(iconKey, out var glyph) ? glyph : null;

    private static Text AddText(Transform parent, string content, ColorRgba? tint)
    {
        var go = new GameObject("Text");
        var t = go.AddComponent<Text>();
        t.text = content; t.alignment = TextAnchor.MiddleCenter; t.fontSize = 18;
        t.color = tint is { } c ? new Color(c.R, c.G, c.B, c.A) : Color.white;
        try { t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* box still shows */ }
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return t;
    }

    // The rail-button label, styled to MATCH the native rail items: copies the
    // game's TMP font asset + material + colour + size from the template item's
    // own label (the game uses TextMeshPro). Falls back to a uGUI Text (Arial) if
    // the template has no TMP label. raycastTarget off so the button's surface
    // owns the click.
    private static void AddRailLabel(Transform parent, string content, Vector2 anchorMin, Vector2 anchorMax, Transform template)
    {
        var src = template != null ? template.GetComponentInChildren<TMP_Text>(true) : null;
        if (src == null)
        {
            AddTextRegion(parent, content, anchorMin, anchorMax, 13);
            return;
        }
        var go = new GameObject("Label");
        var t = go.AddComponent<TextMeshProUGUI>();
        t.font = src.font;
        t.fontSharedMaterial = src.fontSharedMaterial;   // exact look (outline/glow the game uses)
        t.color = src.color;
        t.fontStyle = src.fontStyle;
        t.fontSize = src.fontSize;
        t.enableAutoSizing = src.enableAutoSizing;
        t.fontSizeMin = src.fontSizeMin;
        t.fontSizeMax = src.fontSizeMax;
        t.alignment = TextAlignmentOptions.Center;
        t.raycastTarget = false;
        t.text = content;
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // A Text laid out in a sub-region of its parent (icon row / label row of a
    // rail button), overflow-friendly so a single glyph isn't clipped.
    private static Text AddTextRegion(Transform parent, string content, Vector2 anchorMin, Vector2 anchorMax, int fontSize)
    {
        var go = new GameObject("Text");
        var t = go.AddComponent<Text>();
        t.text = content; t.alignment = TextAnchor.MiddleCenter; t.fontSize = fontSize; t.color = Color.white;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        try { t.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch { /* box still shows */ }
        go.transform.SetParent(parent, worldPositionStays: false);
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return t;
    }
}
