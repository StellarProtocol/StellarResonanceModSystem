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
internal sealed partial class WindowRenderer : IWindowRenderer, IWindowOrder, IWindowCanvasMetrics
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

    // The shared OS dynamic font repacks its glyph atlas when a text-heavy panel requests many glyphs; that
    // strands earlier/hidden Text with stale UVs (garbled glyphs). Refresh every window's text on rebuild.
    private void OnFontTextureRebuilt(Font f)
    {
        if (_assets.MenuFont == null || f != _assets.MenuFont) return;
        for (var i = 0; i < _tokens.Count; i++) _tokens[i].RefreshFontTexture();
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
            // Click-away popups: let the per-frame ticker dismiss this window (Escape / press outside its rect).
            // The dismiss runs OnClose — the same path the ✕ uses — so IsShown stays in sync.
            if (reg.Spec.DismissOnOutsideClick && reg.OnClose is { } onClose && _ticker != null && token.Rect != null)
                _ticker.Dismissables.Add((token.Rect, onClose));
            // Register every window root (popup or regular) so FrontWindowBlocks can check z-order overlap.
            if (_ticker != null && token.Rect != null)
                _ticker.WindowRoots.Add(token.Rect);
            // Deterministic stacking so draw order doesn't depend on plugin load / mount order. The plugin sets
            // ZOrder to control its own placement; Category is the default tiebreak (HUD behind Tools behind
            // Debug); click-away popups always sit on top. Without this, whichever window mounted last drew on
            // top — so e.g. the combat meter jumped above other plugins' panels after a redeploy shifted order.
            token.ZOrder = reg.Spec.ZOrder;
            token.ZCat = (int)reg.Spec.Category;
            token.ZPopup = reg.Spec.DismissOnOutsideClick;
            token.ZSeq = _zseq++;
            ReorderWindows();
            DumpRects(token, reg.Spec.Id);   // .Diagnostics.cs — self-gated on STELLAR_DIAGNOSTICS, else no-op
            return token;
        }
        catch (Exception ex) { _log.Warning($"[Window] mount '{reg.Spec.Id}' threw: {ex.Message}"); return null; }
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
        if (!hideAll) t.Apply();
    }

    public void SetRect(object? token, WindowRect rect)
    {
        if (token is not WindowToken t || t.Rect == null) return;
        // Clamp programmatic placement on-screen the SAME way drags do (WindowInteractionTicker →
        // LayoutStorage.ClampVisible): without this, a default/saved/plugin-supplied position (e.g. CombatMeter's
        // off-screen party-focus x) could drop a window fully off-canvas and unreachable, since drags clamp but
        // SetRect did not. anchoredPosition is (X, -Y) with a top-left anchor.
        var clamped = ClampToScreen(t, rect);
        t.Rect.anchoredPosition = new Vector2(clamped.X, -clamped.Y);
        if (t.Resizable && rect.Width > 0f && rect.Height > 0f)
        {
            var min = Vector2.zero; var max = new Vector2(float.MaxValue, float.MaxValue);
            if (_ticker != null)
                for (var i = 0; i < _ticker.DragResizers.Count; i++)
                    if (_ticker.DragResizers[i].Target == t.Rect) { min = _ticker.DragResizers[i].Min; max = _ticker.DragResizers[i].Max; break; }
            t.Rect.sizeDelta = new Vector2(Mathf.Clamp(rect.Width, min.x, max.x), Mathf.Clamp(rect.Height, min.y, max.y));
        }
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

    // Screen px per canvas unit. GetRect/SetRect speak canvas units (the CanvasScaler applies this factor); the
    // layout editor is uniformly screen-px, so WindowService scales editor rects by this. Mirrors the ClampToScreen guard.
    public float CanvasScale => _canvasComp != null && _canvasComp.scaleFactor > 0f ? _canvasComp.scaleFactor : 1f;

    // A freshly-added CanvasScaler reports the DEFAULT scaleFactor (1.0) on its create frame; the real value lands
    // after willRenderCanvases runs (end of frame). So "settled" iff at least one frame boundary has passed since
    // the (re)create — a frame-elapsed test, NOT a value test (a genuine 1.0 is indistinguishable from the default).
    public bool CanvasScaleReady => _canvasComp != null && Time.frameCount > _canvasCreatedFrame;

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

    /// <summary>Lazily create the Stellar window canvas + its GraphicRaycaster. Re-creates if a scene
    /// change destroyed it (WindowService self-heal then re-mounts each window).</summary>
    // Register the injected managed UnityEngine.Object subclasses with Il2CppInterop, once each, before any
    // AddComponent of them runs. ChartGraphic overrides OnPopulateMesh (vtable virtual) — the spike confirmed
    // native→managed dispatch works under Il2CppInterop 1.5.1; WindowInteractionTicker is a plain MonoBehaviour.
    private void RegisterInjectedTypes()
    {
        if (!_tickerRegistered)
        {
            try { ClassInjector.RegisterTypeInIl2Cpp<WindowInteractionTicker>(); } catch { /* already registered */ }
            _tickerRegistered = true;
        }
        if (!_chartRegistered)
        {
            try { ClassInjector.RegisterTypeInIl2Cpp<ChartGraphic>(); } catch { /* already registered */ }
            _chartRegistered = true;
        }
    }

    private bool EnsureCanvas()
    {
        if (_canvas != null) return true;
        _tokens.Clear();   // (re)creating the canvas — any tokens from a scene-destroyed canvas are dead
        try
        {
            var go = new GameObject("StellarWindowCanvas") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.pixelPerfect = true;
            canvas.sortingOrder = WindowSortingOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = UiRefResolution(
                Mathf.Clamp((_chrome as Stellar.Application.Services.NamedThemeService)?.UiScale ?? 1f, 0.75f, 1.5f));
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;
            // Interactive: ride the game's existing EventSystem; DO NOT create a second one.
            go.AddComponent<GraphicRaycaster>();
            RegisterInjectedTypes();
            _ticker = go.AddComponent<WindowInteractionTicker>();
            // Live UI scale: the ticker polls this each frame and applies it to the CanvasScaler (no rebake).
            _ticker.UiScaleProvider = () => (_chrome as Stellar.Application.Services.NamedThemeService)?.UiScale ?? 1f;
            if (!_fontRebuildHooked) { _onFontRebuilt = OnFontTextureRebuilt; Font.textureRebuilt += _onFontRebuilt; _fontRebuildHooked = true; }
            _canvas = go;
            _canvasComp = canvas;
            _canvasRoot = go.transform;
            _canvasGeneration++;                   // recreate detector (Host reapply trigger)
            _canvasCreatedFrame = Time.frameCount; // scale-ready gate: the scaler settles a frame LATER
            _assets.EnsureBaked(_colors);
            _hudAssets.EnsureBaked(_hudColors);                        // HUD pill/bar 9-slice sprites + shadowed HudText (HudOverlay windows)
            _assets.OpacityProvider = () => _chrome.WindowOpacity;     // live frame-alpha tint (no rebake/flicker)
            _assets.FontScaleProvider = () => _chrome.FontScale;        // live uGUI text scaling
            _assets.ButtonStyleProvider = () => _chrome.ButtonStyle;    // global Button style → window buttons
            _assets.ScrollbarStyleProvider = () => _chrome.ScrollbarStyle;
            // Per-frame field tick (cursor/Esc) + ColorPicker SV/hue drag are driven by the ticker.
            _builder = new WindowBuilder(_assets,
                registerField: f => _ticker!.Fields.Add(f),
                registerDrag: (area, pick) => _ticker!.DragAreas.Add((area, pick)),
                registerWindowDrag: (handle, target, editOnly) => _ticker!.DragWindows.Add((handle, target, editOnly)),
                registerHover: (cell, set) => _ticker!.Hovers.Add((cell, set)),
                registerPulse: p => _ticker!.Pulses.Add(p));
            WireBuilderHooks();
            _log.Info("[Window] Stellar window canvas created");
            return true;
        }
        catch (Exception ex) { _log.Error($"[Window] canvas create threw: {ex.Message}"); _canvas = null; return false; }
    }

    // Wire the builder's sandbox-pure registration callbacks onto the live ticker. Split out of EnsureCanvas
    // to keep it under the 50-LoC gate; runs once per canvas create (the ticker is non-null here).
    private void WireBuilderHooks()
    {
        _builder!.IconResolver = Stellar.Infrastructure.UI.LauncherIcons.Get;   // chrome glyphs (star/…) for tiles
        _builder.HudAssets = _hudAssets;   // HudOverlay leaf sprites/colours (stable object; rebaked in place on theme change)
        _builder.RegisterResize = (grip, target, min, max, editOnly) => _ticker!.DragResizers.Add((grip, target, min, max, editOnly));
        _builder.RegisterDragSlot = (cell, key, canDrag, hover) => _ticker!.DragSlots.Add((cell, key, canDrag, hover));
        _builder.SetDragSlotDrop = onDrop => { if (_ticker != null) _ticker.DragSlotDrop = onDrop; };
        _builder.RegisterRightClick = (cell, cb) => _ticker!.RightClicks.Add((cell, cb));
        _builder.RegisterDismissable = (root, dismiss) => _ticker!.Dismissables.Add((root, dismiss));
        _builder.RegisterRenderHost = (img, fn, drag, zoom, pan, resize) => _ticker!.RenderHosts.Add((img, fn, drag, zoom, pan, resize));
        _builder.RegisterGameTexture = (img, fn, uv, boxW, boxH) => _ticker!.IconHosts.Add(
            new WindowInteractionTicker.IconHost { Img = img, Texture = fn, Uv = uv, BoxW = boxW, BoxH = boxH });
        _builder.RegisterScrollbar = rt => _ticker!.ScrollbarRects.Add(rt);
        _builder.RegisterChartPan = (plot, get, set, total, minSpan)
            => _ticker!.ChartPans.Add(MakeChartPan(plot, get, set, total, minSpan));
        _builder.RegisterChartNav = new WindowBuilder.ChartNavRegistrar(reg => _ticker!.ChartNavs.Add(
            new WindowInteractionTicker.ChartNav
            {
                Nav = reg.Nav, Left = reg.Left, Right = reg.Right, Body = reg.Body,
                Get = reg.Get, Set = reg.Set, Total = reg.Total, MinSpan = reg.MinSpan,
            }));
    }

    // Build a ChartPan entry, computing the scroll-pipeline guard ONCE (plot is live in the hierarchy here):
    // a chart nested in a ScrollRect yields the wheel to the scroll instead of zooming (see ChartPan.cs).
    private static WindowInteractionTicker.ChartPan MakeChartPan(
        RectTransform plot, Func<(float, float)> get, Action<(float, float)> set, Func<float> total, Func<float> minSpan)
        => new()
        {
            Plot = plot, Get = get, Set = set, Total = total, MinSpan = minSpan,
            InsideScrollRect = plot.GetComponentInParent<UnityEngine.UI.ScrollRect>() != null,
        };
}
