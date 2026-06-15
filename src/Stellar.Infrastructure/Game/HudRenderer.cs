using System;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Unity;
using UnityEngine;
using HudToken = Stellar.Infrastructure.Game.HudElementBuilder.HudToken;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IL2CPP host for the native uGUI HUD: owns the dedicated Stellar screen-space-overlay
/// canvas (one canvas, HideAndDontSave + DontDestroyOnLoad, self-heals on scene change),
/// the per-frame bar animator, and the theme-asset lifetime. The element-tree geometry
/// itself is built by the IL2CPP-free <see cref="HudElementBuilder"/> (shared with the UI
/// sandbox); this class wires it to the canvas and the <see cref="HudBarAnimator"/>.
/// </summary>
internal sealed class HudRenderer : IHudRenderer
{
    private const int HudSortingOrder = 32750;   // above game world UI, below the input blocker (32760)

    private readonly IPluginLog _log;
    private readonly IThemeHudColors _colors;
    private readonly HudThemeAssets _assets = new();
    private GameObject? _canvas;
    private Canvas? _canvasComp;   // cached for the perf-overlay Master HUD kill (toggles Canvas.enabled)
    private Transform? _canvasRoot;
    private HudBarAnimator? _animator;
    private HudElementBuilder? _builder;
    private bool _typeRegistered;

    public HudRenderer(IPluginLog log, IThemeHudColors colors) { _log = log; _colors = colors; }

    /// <summary>
    /// Active-theme switch: rebake the HUD sprites from the new palette and drop
    /// the canvas. HudService's per-tick self-heal then re-mounts every HUD on the
    /// fresh canvas, picking up the new sprites/colours — no HudService change.
    /// </summary>
    public void InvalidateTheme()
    {
        _assets.Rebake(_colors);
        DropCanvas();
    }

    /// <summary>Framework teardown: destroy the canvas (and its HUD children) + the baked assets.</summary>
    public void Shutdown()
    {
        DropCanvas();
        _assets.DestroyAll();
    }

    private void DropCanvas()
    {
        if (_canvas != null) UnityEngine.Object.Destroy(_canvas);
        _canvas = null;
        _canvasComp = null;
        _canvasRoot = null;
        _animator = null;
        _builder = null;
    }

    public bool IsAnchorAvailable(HudAnchor anchor) => anchor == HudAnchor.FreeOverlay && EnsureCanvas();

    public object? Mount(HudSpec spec)
    {
        if (!EnsureCanvas() || _canvasRoot == null || _builder == null) return null;
        try { return _builder.Build(spec, _canvasRoot); }
        catch (Exception ex) { _log.Warning($"[Hud] mount '{spec.Id}' threw: {ex.Message}"); return null; }
    }

    public bool IsAlive(object? token) => token is HudToken t && t.Root != null;

    /// <summary>Per-tick bar smoothing, driven from HudService.Tick (the throttled framework ticker)
    /// instead of an injected MonoBehaviour.Update — avoids a per-frame IL2CPP managed crossing.</summary>
    public void TickAnimations(float dt) => _animator?.Step(dt);

    public void ApplyValues(object? token, HudSpec spec, bool hide)
    {
        // Perf-overlay Master HUD kill: toggle Canvas.enabled (stops rendering without tearing down the
        // hierarchy → no remount). Driven from the per-tick apply path (≤100 ms latency on the cap); cheap/
        // idempotent. Folded here rather than widening IHudRenderer.
        if (_canvasComp != null) _canvasComp.enabled = !PerfControls.MasterHudKill;
        if (token is HudToken t && t.Root != null)
        {
            // hide := the service's policy gate (auto-hide behind a full-screen game menu). Deactivate the HUD
            // root (no remount) and skip the value pull when hidden.
            if (t.Root.activeSelf == hide) t.Root.SetActive(!hide);
            if (!hide) t.Apply();
        }
    }

    public void SetRect(object? token, WindowRect rect)
    {
        if (token is not HudToken t || t.Rect == null) return;
        t.Rect.anchoredPosition = new Vector2(rect.X, -rect.Y);   // top-left anchor, y-down screen space
    }

    public WindowRect GetRect(object? token)
    {
        if (token is not HudToken t || t.Rect == null) return default;
        var p = t.Rect.anchoredPosition;
        var size = t.Rect.rect;
        return new WindowRect(p.x, -p.y, size.width, size.height);
    }

    public void Destroy(object? token)
    {
        if (token is HudToken t && t.Root != null) UnityEngine.Object.Destroy(t.Root);
        _animator?.Prune();
    }

    /// <summary>Lazily create the Stellar HUD canvas + the per-frame bar animator. Re-creates if a
    /// scene change destroyed it (HudService self-heal then re-mounts each HUD).</summary>
    private bool EnsureCanvas()
    {
        if (_canvas != null) return true;
        try
        {
            var go = new GameObject("StellarHudCanvas") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Snap graphics to the pixel grid: layout centring (e.g. the 16 px name
            // slot vertically centred in the taller pill row) otherwise lands text on
            // half-pixels, blurring the dynamic-font glyphs. pixelPerfect rounds them.
            canvas.pixelPerfect = true;
            canvas.sortingOrder = HudSortingOrder;
            _canvasComp = canvas;   // cached for Master HUD kill (Canvas.enabled toggle)
            if (!_typeRegistered)
            {
                try { ClassInjector.RegisterTypeInIl2Cpp<HudBarAnimator>(); } catch { /* already registered */ }
                _typeRegistered = true;
            }
            _animator = go.AddComponent<HudBarAnimator>();
            _canvas = go;
            _canvasRoot = go.transform;
            _assets.EnsureBaked(_colors);   // pill/bar 9-slice sprites + HUD text colours
            _builder = new HudElementBuilder(_assets, (fill, target) => _animator?.Bars.Add((fill, target)));
            _log.Info("[Hud] Stellar HUD canvas created");
            return true;
        }
        catch (Exception ex) { _log.Error($"[Hud] canvas create threw: {ex.Message}"); _canvas = null; return false; }
    }
}
