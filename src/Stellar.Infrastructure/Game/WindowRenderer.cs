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
internal sealed partial class WindowRenderer : IWindowRenderer
{
    // Above HUDs (32750), below the input blocker (32760) — windows draw over HUDs, blocker over all.
    private const int WindowSortingOrder = 32755;

    private readonly IPluginLog _log;
    private readonly IThemeMenuColors _colors;
    private readonly IChromeStyle _chrome;          // supplies the active theme's per-preset window opacity
    private readonly WindowThemeAssets _assets = new();
    private GameObject? _canvas;
    private Transform? _canvasRoot;
    private WindowBuilder? _builder;
    private WindowInteractionTicker? _ticker;
    private bool _tickerRegistered;
    private bool _fontRebuildHooked;
    private Action<Font>? _onFontRebuilt;   // cached delegate so subscribe/unsubscribe match (IL2CPP event)
    private readonly System.Collections.Generic.List<WindowToken> _tokens = new();   // live windows, for in-place re-skin

    // The shared OS dynamic font repacks its glyph atlas when a text-heavy panel requests many glyphs; that
    // strands earlier/hidden Text with stale UVs (garbled glyphs). Refresh every window's text on rebuild.
    private void OnFontTextureRebuilt(Font f)
    {
        if (_assets.MenuFont == null || f != _assets.MenuFont) return;
        for (var i = 0; i < _tokens.Count; i++) _tokens[i].RefreshFontTexture();
    }

    public WindowRenderer(IPluginLog log, IThemeMenuColors colors, IChromeStyle chrome)
    { _log = log; _colors = colors; _chrome = chrome; }

    /// <summary>Active-theme switch: rebake the window sprites + RE-SKIN every mounted window IN PLACE (new
    /// sprites/colours/sizes onto the existing GameObjects). No canvas drop → no 1-frame flicker, and the
    /// change shows live (uGUI is retained-mode; this is the equivalent of IMGUI's free per-frame repaint).</summary>
    public void InvalidateTheme()
    {
        if (_canvas == null) return;   // nothing mounted yet — the next mount bakes fresh
        _assets.Rebake(_colors);
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
    }

    private void DropCanvas()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        _canvas = null;
        _canvasRoot = null;
        _builder = null;
        _ticker = null;
        _tokens.Clear();   // canvas + all window GOs gone; WindowService self-heal re-mounts (re-adds tokens)
    }

    public bool IsCanvasAvailable() => EnsureCanvas();

    public object? Mount(WindowRegistration reg)
    {
        if (!EnsureCanvas() || _canvasRoot == null || _builder == null) return null;
        try
        {
            var token = _builder.Build(reg, _canvasRoot);
            _tokens.Add(token);   // track for in-place re-skin on theme change
            DumpRects(token, reg.Spec.Id);   // .Diagnostics.cs — self-gated on STELLAR_DIAGNOSTICS, else no-op
            return token;
        }
        catch (Exception ex) { _log.Warning($"[Window] mount '{reg.Spec.Id}' threw: {ex.Message}"); return null; }
    }

    public bool IsAlive(object? token) => token is WindowToken t && t.Root != null;

    public void ApplyValues(object? token, WindowRegistration reg, bool hide)
    {
        if (token is not WindowToken t || t.Root == null) return;
        // hide := the service's policy gate (auto-hide behind a full-screen game menu / hide-until-in-world).
        // Combine with the perf-overlay Master HUD kill (dev toggle: hide HUD-category windows only — the perf
        // overlay + Settings are Tools on THIS canvas, so a whole-canvas kill would hide the toggle, a trap).
        // SetActive the root (no remount); skip Apply when hidden.
        var hideAll = hide || (PerfControls.MasterHudKill && reg.Spec.Category == Stellar.Abstractions.Domain.WindowCategory.HUD);
        if (t.Root.activeSelf == hideAll) t.Root.SetActive(!hideAll);
        if (!hideAll) t.Apply();
    }

    public void SetRect(object? token, WindowRect rect)
    {
        if (token is not WindowToken t || t.Rect == null) return;
        t.Rect.anchoredPosition = new Vector2(rect.X, -rect.Y);   // top-left anchor, y-down screen space
        if (t.Resizable && rect.Width > 0f && rect.Height > 0f)
            t.Rect.sizeDelta = new Vector2(rect.Width, rect.Height);
    }

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
            if (t.Root != null) UnityEngine.Object.Destroy(t.Root);
        }
        _ticker?.Prune();   // drop drag/hover areas whose RectTransform was destroyed
    }

    /// <summary>Lazily create the Stellar window canvas + its GraphicRaycaster. Re-creates if a scene
    /// change destroyed it (WindowService self-heal then re-mounts each window).</summary>
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
            // Interactive: ride the game's existing EventSystem; DO NOT create a second one.
            go.AddComponent<GraphicRaycaster>();
            if (!_tickerRegistered)
            {
                try { ClassInjector.RegisterTypeInIl2Cpp<WindowInteractionTicker>(); } catch { /* already registered */ }
                _tickerRegistered = true;
            }
            _ticker = go.AddComponent<WindowInteractionTicker>();
            if (!_fontRebuildHooked) { _onFontRebuilt = OnFontTextureRebuilt; Font.textureRebuilt += _onFontRebuilt; _fontRebuildHooked = true; }
            _canvas = go;
            _canvasRoot = go.transform;
            _assets.EnsureBaked(_colors);
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
            _builder.IconResolver = Stellar.Infrastructure.UI.LauncherIcons.Get;   // chrome glyphs (star/…) for tiles
            _builder.RegisterResize = (grip, target, min, max) => _ticker!.DragResizers.Add((grip, target, min, max));
            _builder.RegisterDragSlot = (cell, key, canDrag, hover) => _ticker!.DragSlots.Add((cell, key, canDrag, hover));
            _builder.SetDragSlotDrop = onDrop => { if (_ticker != null) _ticker.DragSlotDrop = onDrop; };
            _builder.RegisterRightClick = (cell, cb) => _ticker!.RightClicks.Add((cell, cb));
            _builder.RegisterRenderHost = (img, fn, drag, zoom, pan, resize) => _ticker!.RenderHosts.Add((img, fn, drag, zoom, pan, resize));
            _builder.RegisterGameTexture = (img, fn, uv, boxW, boxH) => _ticker!.IconHosts.Add(
                new WindowInteractionTicker.IconHost { Img = img, Texture = fn, Uv = uv, BoxW = boxW, BoxH = boxH });
            _builder.RegisterScrollbar = rt => _ticker!.ScrollbarRects.Add(rt);
            _log.Info("[Window] Stellar window canvas created");
            return true;
        }
        catch (Exception ex) { _log.Error($"[Window] canvas create threw: {ex.Message}"); _canvas = null; return false; }
    }
}
