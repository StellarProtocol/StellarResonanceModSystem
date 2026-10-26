using System;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;
using UnityEngine.UI;
using Stellar.Abstractions.Domain;
using WindowToken = Stellar.Infrastructure.Game.WindowBuilder.WindowToken;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IL2CPP host for the native uGUI interactive windows: owns the dedicated Stellar screen-space-overlay
/// canvas (HideAndDontSave + DontDestroyOnLoad, self-heals on scene change) plus its
/// <see cref="GraphicRaycaster"/> so widgets receive pointer events — riding the game's EXISTING
/// EventSystem (no second EventSystem is created). The element-tree + chrome geometry is built by the
/// IL2CPP-free <see cref="WindowBuilder"/> (shared with the UI sandbox); this class wires it to the canvas.
/// Mirrors <see cref="HudRenderer"/>; the HUD path is untouched.
/// </summary>
internal sealed partial class WindowRenderer : IWindowRenderer, IWindowOrder, IWindowCanvasMetrics, IWindowParking, IWindowPopups, IWindowAutoFocus
{
    // Above HUDs (32750), below the input blocker (32760) — windows draw over HUDs, blocker over all.
    private const int WindowSortingOrder = 32755;

    // Single source of the UI-scale → CanvasScaler formula. Dividing BOTH reference dimensions by the user factor
    // u multiplies the resulting scaleFactor by exactly u, so the whole window canvas scales by u. Used on first
    // mount (EnsureCanvas, avoids a 1-frame pop) and by the ticker's per-frame poll (WindowInteractionTicker).
    internal static Vector2 UiRefResolution(float uiScale)
        => new Vector2(2560f / uiScale, 1440f / uiScale);

    private readonly IPluginLog _log;
    private readonly IThemeMenuColors _colors;
    private readonly IThemeHudColors _hudColors;    // HUD palette — baked into _hudAssets for SurfaceStyle.HudOverlay windows
    private readonly IChromeStyle _chrome;          // supplies the active theme's per-preset window opacity
    private readonly WindowThemeAssets _assets = new();
    // HUD sprite/colour set (rounded pill + bar 9-slice, shadowed HudText), baked from the SAME IThemeHudColors
    // as HudRenderer's copy and re-baked on the SAME ActiveChanged signal. Threaded into WindowBuilder so a
    // window declaring SurfaceStyle.HudOverlay reproduces the native HUD look byte-for-byte. Distinct object from
    // HudRenderer's — the two renderers own separate canvases; HudThemeAssets is NOT deleted, it lives on here too.
    private readonly HudThemeAssets _hudAssets = new();
    private GameObject? _canvas;
    private Canvas? _canvasComp;
    private Transform? _canvasRoot;
    private WindowBuilder? _builder;
    private WindowInteractionTicker? _ticker;
    private bool _tickerRegistered;
    private bool _chartRegistered;   // ChartGraphic (injected MaskableGraphic) registered with Il2CppInterop?
    private bool _fontRebuildHooked;
    private Action<Font>? _onFontRebuilt;   // cached delegate so subscribe/unsubscribe match (IL2CPP event)
    private readonly System.Collections.Generic.List<WindowToken> _tokens = new();   // live windows, for in-place re-skin
    private int _zseq;    // monotonic mount counter — drives ZSeq (stable tiebreak within same ZFront tier)
    private int _zfront;  // monotonic BringToFront counter — drives ZFront (overrides ZCat/ZSeq when non-zero)
    private int _canvasGeneration;         // bumps on every canvas (re)create — Host fires a settled reapply on change
    private int _canvasCreatedFrame = -1;  // Time.frameCount at the last (re)create; scale settles a FRAME later

    // A dynamic font repacks its glyph atlas when a text-heavy panel requests many glyphs; that strands
    // earlier/hidden Text with stale UVs (garbled glyphs). Refresh every window's text ON THAT FONT on rebuild.
    // No MenuFont-only filter: window Text also lives on ConfigureText's builtin font (HudOverlay text keeps it
    // for its native-HUD look), and that font's repacks were ignored → garbled HUD-overlay glyphs.
    private void OnFontTextureRebuilt(Font f)
    {
        if (f == null) return;
        var hitchT = Stellar.Abstractions.Diagnostics.HitchProbe.Begin();   // count + cost of atlas rebuilds per hitch frame
        var refreshed = 0;
        for (var i = 0; i < _tokens.Count; i++) refreshed += _tokens[i].RefreshFontTexture(f);
        Stellar.Abstractions.Diagnostics.HitchProbe.End("font-rebuild", hitchT);
        LogFontRebuild(f, refreshed);
    }

    public WindowRenderer(IPluginLog log, IThemeMenuColors colors, IThemeHudColors hudColors, IChromeStyle chrome)
    { _log = log; _colors = colors; _hudColors = hudColors; _chrome = chrome; }

    /// <summary>Active-theme switch: rebake the window sprites + RE-SKIN every mounted window IN PLACE (new
    /// sprites/colours/sizes onto the existing GameObjects). No canvas drop → no 1-frame flicker, and the
    /// change shows live (uGUI is retained-mode; this is the equivalent of IMGUI's free per-frame repaint).</summary>
    public void InvalidateTheme()
    {
        if (_canvas == null) return;   // nothing mounted yet — the next mount bakes fresh
        _assets.Rebake(_colors);
        // Rebake the HUD sprite set too (destroys the prior sprites), then the per-window Reskin() below re-points
        // every HudOverlay leaf's sprite/colour to the fresh ones IN PLACE — mirroring how HudRenderer rebakes on
        // the same signal (it drops its canvas + remounts; we reskin, since the window path never drops on theme change).
        _hudAssets.Rebake(_hudColors);
        for (var i = 0; i < _tokens.Count; i++)
        {
            var t = _tokens[i];
            if (t.Root != null) try { t.Reskin(); } catch (Exception ex) { _log.Warning($"[Window] reskin threw: {ex.Message}"); }
        }
    }

    /// <summary>Framework teardown: destroy the canvas (+ its window children) + the baked assets.</summary>
    public void Shutdown()
    {
        if (_fontRebuildHooked && _onFontRebuilt != null) { Font.textureRebuilt -= _onFontRebuilt; _fontRebuildHooked = false; }
        ShutdownStickyCursor();   // before the canvas goes: release the free cursor + re-enable the game's mouse/Esc now
        DropCanvas();
        _assets.DestroyAll();
        _hudAssets.DestroyAll();
    }

    private void DropCanvas()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        _canvas = null;
        _canvasRoot = null;
        _builder = null;
        _ticker = null;
        _tokens.Clear();   // canvas + all window GOs gone; WindowService self-heal re-mounts (re-adds tokens)
        _zseq = 0;
        _zfront = 0;
    }

    public bool IsCanvasAvailable() => EnsureCanvas();

    public object? Mount(WindowRegistration reg)
    {
        if (!EnsureCanvas() || _canvasRoot == null || _builder == null) return null;
        try
        {
            var token = _builder.Build(reg, _canvasRoot);
            _tokens.Add(token);   // track for in-place re-skin on theme change
            AttachNestedCanvas(token, reg.Spec);   // OverrideSortingOrder / IsolateCanvas → own Canvas (+ raycaster)
            // Click-away popups: let the per-frame ticker dismiss this window (Escape / press outside its rect).
            // The dismiss runs OnClose — the same path the ✕ uses — so IsShown stays in sync.
            if (reg.Spec.DismissOnOutsideClick && reg.OnClose is { } onClose && _ticker != null && token.Rect != null)
                _ticker.Dismissables.Add((token.Rect, onClose));
            // Register every window root (popup or regular) so FrontWindowBlocks can check z-order overlap.
            if (_ticker != null && token.Rect != null)
            {
                _ticker.WindowRoots.Add(token.Rect);
                // Sticky cursor engages only on interactive (non-HUD) windows — WindowCategory, not ZOrder, decides.
                if (reg.Spec.Category != Stellar.Abstractions.Domain.WindowCategory.HUD) _ticker.InteractiveRoots.Add(token.Rect);
            }
            // Deterministic stacking so draw order doesn't depend on plugin load / mount order. The plugin sets
            // ZOrder to control its own placement; Category is the default tiebreak (HUD behind Tools behind
            // Debug); click-away popups always sit on top. Without this, whichever window mounted last drew on
            // top — so e.g. the combat meter jumped above other plugins' panels after a redeploy shifted order.
            token.ZOrder = reg.Spec.ZOrder;
            token.ZCat = (int)reg.Spec.Category;
            token.ZPopup = reg.Spec.DismissOnOutsideClick;
            token.ZSeq = _zseq++;
            ReorderWindows();
            ApplyContentMinWidth(token, force: true);   // before ApplySavedRect, so the restore clamps to it (.ContentMinWidth.cs)
            DumpRects(token, reg.Spec.Id);   // .Diagnostics.cs — self-gated on STELLAR_DIAGNOSTICS, else no-op
            return token;
        }
        catch (Exception ex) { _log.Warning($"[Window] mount '{reg.Spec.Id}' threw: {ex.Message}"); return null; }
    }

    // Per-window nested Canvas — two independent reasons to give ONE window its own Canvas:
    //   • OverrideSortingOrder: lift it onto an ABSOLUTE sortingOrder (overrideSorting=true), independent of the
    //     shared-canvas sibling z — so it can draw ABOVE sibling-ordered overlays on higher canvases (the layout-edit
    //     chrome at 32758) while staying below the top input blocker (32760). overrideSorting opts THIS child out of
    //     sibling-order z only; IsPointerOver* read its order.
    //   • IsolateCanvas: make it a BATCHING BOUNDARY only (overrideSorting LEFT false). A Canvas is where uGUI rebuilds
    //     its vertex batch, so a per-frame change inside this window (a ScrollRect moving its content) rebuilds ONLY
    //     this window's batch instead of dirtying + rebatching the whole shared overlay (and every graphic on it) each
    //     render frame. With overrideSorting=false it inherits the parent canvas's sorting and still orders by sibling
    //     index — so ReorderWindows / BringToFront / FrontWindowBlocks (all sibling-index based) keep working
    //     unchanged, and no other window's z is perturbed.
    // Either way the nested Canvas needs its OWN GraphicRaycaster or its widgets become un-hittable (the parent
    // raycaster skips a child canvas) — the override toolbar's buttons/drag AND chat's buttons/scroll rely on it.
    private static void AttachNestedCanvas(WindowToken token, Stellar.Abstractions.Domain.WindowSpec spec)
    {
        if (token.Root == null || (spec.OverrideSortingOrder is null && !spec.IsolateCanvas)) return;
        var oc = token.Root.AddComponent<Canvas>();
        if (spec.OverrideSortingOrder is { } so)
        {
            oc.overrideSorting = true;   // absolute order; opts out of sibling-order z (override path only)
            oc.sortingOrder = so;
        }
        // IsolateCanvas-only path: overrideSorting stays at its default (false) → batching boundary that still
        // sorts by sibling index within the shared parent canvas (z unchanged).
        token.Root.AddComponent<GraphicRaycaster>();
    }

    private const int PopupOrder = 1_000_000;   // click-away popups draw above any plugin ZOrder
    private readonly System.Collections.Generic.List<WindowToken> _zsort = new();

    // Re-assign sibling indices so draw order follows: popups → ZOrder → ZFront (explicit front, beats category)
    // → ZCat → ZSeq. ZFront=0 means never explicitly fronted; non-zero overrides ZCat so BringToFront works
    // cross-category (e.g. an HUD window can surface above a Tools window that normally wins on ZCat).
    private void ReorderWindows()
    {
        _zsort.Clear();
        for (var i = 0; i < _tokens.Count; i++) if (_tokens[i].Rect != null) _zsort.Add(_tokens[i]);
        _zsort.Sort((a, b) =>
        {
            var pa = a.ZPopup ? PopupOrder : a.ZOrder;
            var pb = b.ZPopup ? PopupOrder : b.ZOrder;
            if (pa != pb) return pa - pb;
            if (a.ZFront != b.ZFront) return a.ZFront - b.ZFront;   // explicit front overrides category
            if (a.ZCat != b.ZCat) return a.ZCat - b.ZCat;
            return a.ZSeq - b.ZSeq;
        });
        for (var i = 0; i < _zsort.Count; i++) _zsort[i].Rect.SetSiblingIndex(i);
    }

    public bool IsAlive(object? token) => token is WindowToken t && t.Root != null;

    // IWindowParking: a SetVisible(false) window keeps its built tree, deactivated (the ticker skips every
    // !activeInHierarchy drag/hover/dismiss/field entry, exactly as for a ShouldRender-hidden window). The next
    // ApplyValues re-activates it (and re-arms its first layout via ResetLayout).
    public void Park(object? token)
    {
        if (token is WindowToken t && t.Root != null && t.Root.activeSelf) t.Root.SetActive(false);
    }

    // A fresh mount used to land on top of its stacking tier (new ZSeq); a re-shown parked window does the same.
    public void Unpark(object? token)
    {
        if (token is not WindowToken t) return;
        t.ZSeq = _zseq++;
        ReorderWindows();
    }

    public void BringToFront(object? token)
    {
        if (token is not WindowToken t) return;
        t.ZFront = ++_zfront;
        ReorderWindows();
    }

    public void ApplyValues(object? token, WindowRegistration reg, bool hide)
    {
        if (token is not WindowToken t || t.Root == null) return;
        // hide := the service's policy gate (auto-hide behind a full-screen game menu / hide-until-in-world).
        // Combine with the perf-overlay Master HUD kill (dev toggle: hide HUD-category windows only — the perf
        // overlay + Settings are Tools on THIS canvas, so a whole-canvas kill would hide the toggle, a trap).
        // SetActive the root (no remount); skip Apply when hidden.
        // Layout edit-mode force-show (transient — mirrors the MasterHudKill layering point): while editing, no
        // window is draw-suppressed, so every registered overlay renders (root active) and is grabbable / movable /
        // resizable via its grip + drag handle (which require activeInHierarchy — WindowInteractionTicker 357/404).
        // This beats BOTH the plugin ShouldRender content-gate AND the MasterHudKill dev toggle. It mutates no
        // persisted or SetVisible state — exiting edit mode reverts each window to its real gated state on the next
        // apply (~10 Hz). Inert (byte-identical) when not editing (`&& true`). LayoutEditGate.IsEditing is synced
        // from LayoutEditorService.IsEditing each tick (LayoutEditorOverlay.TickInput) — the SAME flag the ticker's
        // grip/handle gate reads, so what renders and what's draggable stay consistent. A SetVisible(false) window
        // is unmounted upstream in WindowService.TickEntry and never reaches here, so it stays hidden by design.
        // Scope: the force-show applies ONLY to EditModeDragOnly overlays (the arrangeable HUDs the layout editor
        // manages via WindowService.EditableElements). Free-drag Tools dialogs (e.g. the login-screen switcher,
        // Settings, History) are not edit-managed, so they stay gated by their own ShouldRender even in edit mode —
        // otherwise they'd render with no outline/handle: visible but not arrangeable.
        var hideAll = (hide || (PerfControls.MasterHudKill && reg.Spec.Category == Stellar.Abstractions.Domain.WindowCategory.HUD))
                      && !(LayoutEditGate.IsEditing && reg.Spec.EditModeDragOnly);
        var wasHidden = !t.Root.activeSelf;
        if (t.Root.activeSelf == hideAll) t.Root.SetActive(!hideAll);
        // A window re-shown after being hidden re-arms its immediate first-layout, so a content-sized popup
        // (e.g. the cursor-positioned row context menu) is rebuilt to its true size the same frame it reappears
        // — otherwise it shows one frame at the previous open's size, which mis-clamps its on-screen position.
        if (!hideAll && wasHidden) t.ResetLayout();
        if (hideAll) return;
        var perfT = UiPerfStats.On ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;   // [UiPerf] summary (HITCH only)
        t.Apply(); ApplyContentMinWidth(t, force: false);   // gated re-measure (.ContentMinWidth.cs)
        UiPerfStats.AddApply(reg.Spec.Id, perfT);
    }

    public void SetRect(object? token, WindowRect rect)
    {
        if (token is not WindowToken t || t.Rect == null) return;
        // Clamp programmatic placement on-screen the SAME way drags do (WindowInteractionTicker →
        // LayoutStorage.ClampVisible): without this, a default/saved/plugin-supplied position (e.g. CombatMeter's
        // off-screen party-focus x) could drop a window fully off-canvas and unreachable, since drags clamp but
        // SetRect did not. anchoredPosition is (X, -Y) with a top-left anchor. Size is clamped FIRST so the position
        // clamp measures the final width (a content min-width widen can grow it — WindowRenderer.ContentMinWidth.cs).
        if (t.Resizable && rect.Width > 0f && rect.Height > 0f)
        {
            var min = Vector2.zero; var max = new Vector2(float.MaxValue, float.MaxValue);
            if (_ticker != null)
                for (var i = 0; i < _ticker.DragResizers.Count; i++)
                    if (_ticker.DragResizers[i].Target == t.Rect) { min = _ticker.DragResizers[i].Min; max = _ticker.DragResizers[i].Max; break; }
            var size = new Vector2(Mathf.Clamp(rect.Width, min.x, max.x), Mathf.Clamp(rect.Height, min.y, max.y));
            t.Rect.sizeDelta = size;
            rect = KeepWidenedOnScreen(new WindowRect(rect.X, rect.Y, size.x, size.y), rect.Width);
        }
        var clamped = ClampToScreen(t, rect);
        t.Rect.anchoredPosition = new Vector2(clamped.X, -clamped.Y);
    }

    // ClampVisible needs the window SIZE to clamp the right/bottom edge. Position-only callers pass a zero
    // Width/Height; substitute the live RectTransform size so we never clamp against size 0 (which would treat the
    // window as 0px wide and let its left edge pin anywhere). Returns the clamped top-left in WindowRect space.
    private WindowRect ClampToScreen(WindowToken t, WindowRect rect)
    {
        var size = t.Rect!.rect.size;
        var w = rect.Width  > 0f ? rect.Width  : size.x;
        var h = rect.Height > 0f ? rect.Height : size.y;
        var s = _canvasComp != null && _canvasComp.scaleFactor > 0f ? _canvasComp.scaleFactor : 1f;
        return Stellar.Application.Services.LayoutStorage.ClampVisible(
            new WindowRect(rect.X, rect.Y, w, h),
            new Stellar.Abstractions.Domain.Resolution(
                Mathf.RoundToInt(Screen.width / s), Mathf.RoundToInt(Screen.height / s)));
    }

    /// <summary>The window canvas root (<c>StellarWindowCanvas</c>, HideAndDontSave — not findable by
    /// FindObjectsOfType), or null before the first mount. Read by the photo visibility backend.</summary>
    internal GameObject? CanvasObject => _canvas;

    /// <summary>Raised (main thread) each time the window canvas is (re)created — e.g. so a held photo overlay hide
    /// is re-applied to the new canvas.</summary>
    internal event Action? CanvasCreated;

    private void OnCanvasCreated()
    {
        _log.Info("[Window] Stellar window canvas created");
        try { CanvasCreated?.Invoke(); }
        catch (Exception ex) { _log.Warning($"[Window] CanvasCreated handler threw: {ex.Message}"); }
    }

    // Screen px per canvas unit. GetRect/SetRect speak canvas units (the CanvasScaler applies this factor); the
    // layout editor is uniformly screen-px, so WindowService scales editor rects by this. Mirrors the ClampToScreen guard.
    public float CanvasScale => _canvasComp != null && _canvasComp.scaleFactor > 0f ? _canvasComp.scaleFactor : 1f;

    // A freshly-added CanvasScaler reports the DEFAULT scaleFactor (1.0) on its create frame; the real value lands
    // after willRenderCanvases runs (end of frame). So "settled" iff at least one frame boundary has passed since
    // the (re)create — a frame-elapsed test, NOT a value test (a genuine 1.0 is indistinguishable from the default).
    public bool CanvasScaleReady => _canvasComp != null && Time.frameCount > _canvasCreatedFrame;

    // IWindowPopups — close any open dropdown popup (it lives on its own scene-root canvas, so hiding the owning
    // window never tears it down). Null-safe: the builder is null before the first canvas build / after teardown.
    public void CloseOpenPopups() => _builder?.CloseDropdown();

    // Monotonic canvas (re)create counter. The Host watches this to fire ONE corrective layout reapply after a
    // scene-change canvas rebuild, once CanvasScaleReady (belt to WindowService.Layout's per-window defer).
    public int CanvasGeneration => _canvasGeneration;

    // The UI-Scale slider value (concrete-only getter on NamedThemeService, which _chrome IS at runtime). Default
    // window positions divide by this so the slider grows windows in place instead of drifting them (bottom-right).
    public float UiScale => (_chrome as Stellar.Application.Services.NamedThemeService)?.UiScale ?? 1f;

    public WindowRect GetRect(object? token)
    {
        if (token is not WindowToken t || t.Rect == null) return default;
        var p = t.Rect.anchoredPosition;
        var size = t.Rect.rect;
        return new WindowRect(p.x, -p.y, size.width, size.height);
    }

    public bool HasFocusedField(object? token) => token is WindowToken t && t.AnyFieldFocused;

    // IWindowAutoFocus — focus the window's AutoFocus field (InputElement.AutoFocus) on a user-initiated show.
    // true = done (focused, or no AutoFocus field declared); false = a field exists but its GameObject isn't
    // active yet (window still draw-suppressed / one-frame activation gap) → WindowService retries next tick.
    public bool TryFocusAutoField(object? token)
    {
        if (token is not WindowToken t) return true;      // dead/foreign token — nothing to retry
        if (t.AutoFocusField is not { } f) return true;   // window declared no AutoFocus field — done
        return f.TryActivateFocus();                      // false → not active in hierarchy yet, retry
    }

    public void Destroy(object? token)
    {
        if (token is WindowToken t)
        {
            _tokens.Remove(t);
            // Drop this window's text fields from the ticker (else they accumulate unbounded across hide/show
            // cycles — the ticker would iterate ever-growing stale Fields every frame).
            if (_ticker != null)
            {
                for (var i = 0; i < t.Fields.Count; i++) { _ticker.Fields.Remove(t.Fields[i]); try { t.Fields[i].Destroy(); } catch { } }
                for (var i = 0; i < t.Pulses.Count; i++) _ticker.Pulses.Remove(t.Pulses[i]);
            }
            t.DisposeNativeTextures();   // ColorPicker SV/hue bakes (HideAndDontSave — not reclaimed by GO destroy)
            if (_ticker != null && t.Rect != null) _ticker.WindowRoots.Remove(t.Rect);
            if (t.Root != null) UnityEngine.Object.Destroy(t.Root);
        }
        _ticker?.Prune();   // drop drag/hover areas whose RectTransform was destroyed
    }
}
