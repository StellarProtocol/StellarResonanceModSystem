using System;
using Il2CppInterop.Runtime.Injection;
using Stellar.Infrastructure.Unity;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Game;

// Canvas creation half of WindowRenderer (split out of WindowRenderer.cs for the 500-LoC gate — a pure move, no
// behaviour change): Il2CppInterop type registration, the lazy Stellar window canvas + scaler + raycaster + ticker,
// and the builder → ticker registration hooks.
internal sealed partial class WindowRenderer
{
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
            _ticker.HitchLog = _log.Info;   // per-render-frame [Hitch] attribution sink (diagnostics only)
            _ticker.PerFrame = TmpGlyphPrewarm.Tick;   // IWindowHost.PrewarmText: a few queued glyphs per rendered frame
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
                registerWindowDrag: (handle, target, windowId, editOnly) => _ticker!.DragWindows.Add((handle, target, windowId, editOnly)),
                registerHover: (cell, set) => _ticker!.Hovers.Add((cell, set)),
                registerPulse: p => _ticker!.Pulses.Add(p));
            WireBuilderHooks();
            OnCanvasCreated();
            return true;
        }
        catch (Exception ex) { _log.Error($"[Window] canvas create threw: {ex.Message}"); _canvas = null; return false; }
    }

    // Wire the builder's sandbox-pure registration callbacks onto the live ticker. Split out of EnsureCanvas
    // to keep it under the 50-LoC gate; runs once per canvas create (the ticker is non-null here).
    private void WireBuilderHooks()
    {
        // Real-bold titles/headers in every script: install the TMP factory before any window builds
        // (game-only — the Mono sandbox leaves it null and renders the legacy crisp fallback).
        TmpStyledText.Register(_log.Info);
        _builder!.IconResolver = Stellar.Infrastructure.UI.LauncherIcons.Get;   // chrome glyphs (star/…) for tiles
        _builder.Warn = _log.Warning;      // e.g. a throwing WindowSpec.TitleProvider (logged once per window id)
        _builder.HudAssets = _hudAssets;   // HudOverlay leaf sprites/colours (stable object; rebaked in place on theme change)
        _builder.RegisterResize = (grip, target, min, max, windowId, editOnly) => _ticker!.DragResizers.Add((grip, target, min, max, windowId, editOnly));
        _builder.RegisterDragSlot = (cell, key, canDrag, hover) => _ticker!.DragSlots.Add((cell, key, canDrag, hover));
        _builder.SetDragSlotDrop = onDrop => { if (_ticker != null) _ticker.DragSlotDrop = onDrop; };
        _builder.RegisterRightClick = (cell, cb) => _ticker!.RightClicks.Add((cell, cb));
        _builder.RegisterReorderList = reg => _ticker!.ReorderLists.Add(reg);
        _builder.ClickSuppressed = () => _ticker != null && _ticker.ReorderSuppressesClick;
        _builder.RegisterDismissable = (root, dismiss) => _ticker!.Dismissables.Add((root, dismiss));
        _builder.RegisterRenderHost = (img, fn, drag, zoom, pan, resize) => _ticker!.RenderHosts.Add((img, fn, drag, zoom, pan, resize));
        _builder.RegisterGameTexture = (img, fn, uv, boxW, boxH) => _ticker!.IconHosts.Add(
            new WindowInteractionTicker.IconHost { Img = img, Texture = fn, Uv = uv, BoxW = boxW, BoxH = boxH });
        _builder.RegisterScrollbar = rt => _ticker!.ScrollbarRects.Add(rt);
        _builder.RegisterScrollView = rt => _ticker!.ScrollContents.Add(rt);
        _builder.SetVoiceClickExcluded = (rt, on) =>
        {
            if (_ticker == null || rt == null) return;
            if (on) { if (!_ticker.VoiceClickRects.Contains(rt)) _ticker.VoiceClickRects.Add(rt); }
            else _ticker.VoiceClickRects.Remove(rt);
        };
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
