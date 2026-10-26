using System;
using System.Collections.Generic;
using Stellar.Application.Services;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Stellar.Infrastructure.Unity;

// Sticky cursor (opt-in, Settings → Layout): samples this frame's input + pointer target for StickyCursorService, which
// owns the state machine and the game calls. Ticked every rendered frame because a press lives for one frame only.
public sealed partial class WindowInteractionTicker
{
    /// <summary>Host-side inputs for the sticky cursor. Owned by WindowRenderer (outlives a ticker: the canvas is rebuilt
    /// on a scene change) and handed to each new ticker.</summary>
    internal sealed class StickyHooks
    {
        public StickyCursorService Service = null!;
        public Func<bool> Enabled = () => false;
        public Func<bool> WorldActive = () => false;
        /// <summary>IInputShield.IsPointerOverGameUi: the top-most raycast hit is a game canvas (once per frame, shared
        /// with the free camera, which already relies on it).</summary>
        public Func<bool> PointerOverGameUi = () => false;
        public Action<string>? Log;
    }

    internal StickyHooks? Sticky;
    // Roots of non-HUD windows (WindowCategory Tools/Debug), registered by WindowRenderer.Mount next to WindowRoots.
    internal readonly List<RectTransform> InteractiveRoots = new();
    private bool _stickyModuleLogged;

    private void PruneSticky()
    {
        for (var i = InteractiveRoots.Count - 1; i >= 0; i--) if (InteractiveRoots[i] == null) InteractiveRoots.RemoveAt(i);
    }

    private bool AnyFieldFocused()
    {
        for (var i = 0; i < Fields.Count; i++) if (Fields[i].IsFocused) return true;
        return false;
    }

    // fieldFocused is sampled BEFORE the field ticks run, so the Esc that defocuses a field is seen as the field's.
    private void TickStickyCursor(bool fieldFocused)
    {
        var hooks = Sticky;
        if (hooks == null) return;
        try
        {
            var enabled = hooks.Enabled();
            var svc = hooks.Service;
            // Idle + off: no input sampling, but WorldActive still flows so a deferred game-side release can drain.
            if (!enabled && !svc.IsSticky) { svc.Tick(new StickyCursorFrame { WorldActive = hooks.WorldActive(), Pointer = StickyPointer.GameUi }); return; }
            var mouseDown = Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1);
            var f = new StickyCursorFrame
            {
                Enabled = enabled,
                WorldActive = hooks.WorldActive(),
                CursorFree = Cursor.visible && Cursor.lockState != CursorLockMode.Locked,
                MouseDown = mouseDown,
                AnyMouseHeld = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2),
                EscDown = Input.GetKeyDown(KeyCode.Escape),
                EscHeld = Input.GetKey(KeyCode.Escape),
                FieldFocused = fieldFocused,
                AnyInteractiveVisible = AnyInteractiveRootVisible(),
                Pointer = svc.WantsPointer(enabled, mouseDown) ? ClassifyPointer(Input.mousePosition, hooks) : StickyPointer.GameUi,
            };
            var wasSticky = svc.IsSticky;
            svc.Tick(f);
            if (!wasSticky && svc.IsSticky) LogInputModuleOnce(hooks);
        }
        catch (Exception ex) { if (_throwLogged++ == 0) Debug.LogWarning($"[Window] sticky-cursor tick threw (rate-limited): {ex.Message}"); }
    }

    // Stellar windows first (our own hit-test, z-order aware): the front-most window under the pointer decides HUD vs
    // interactive. Only when no Stellar window is there do we ask whether it's the game's interface or the 3D world.
    private StickyPointer ClassifyPointer(Vector2 mp, StickyHooks hooks)
    {
        for (var i = 0; i < InteractiveRoots.Count; i++)
        {
            var root = InteractiveRoots[i];
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            if (!RectTransformUtility.RectangleContainsScreenPoint(root, mp, null)) continue;
            if (FrontWindowBlocks(mp, root)) continue;   // a window in front of it owns this point
            return StickyPointer.StellarInteractive;
        }
        for (var i = 0; i < WindowRoots.Count; i++)
        {
            var root = WindowRoots[i];
            if (root == null || !root.gameObject.activeInHierarchy) continue;
            if (RectTransformUtility.RectangleContainsScreenPoint(root, mp, null)) return StickyPointer.StellarHud;
        }
        return hooks.PointerOverGameUi() ? StickyPointer.GameUi : StickyPointer.World;
    }

    private bool AnyInteractiveRootVisible()
    {
        for (var i = 0; i < InteractiveRoots.Count; i++)
            if (InteractiveRoots[i] != null && InteractiveRoots[i].gameObject.activeInHierarchy) return true;
        return false;
    }

    // Diagnostic for in-game validation: the world-click swallow disables Rewired's mouse only while the pointer is over
    // the world. If the EventSystem's input module reads Rewired (e.g. RewiredStandaloneInputModule) rather than
    // UnityEngine.Input, a click the same frame the pointer crosses onto UI could be lost — this line tells us which.
    private void LogInputModuleOnce(StickyHooks hooks)
    {
        if (_stickyModuleLogged) return;
        _stickyModuleLogged = true;
        var es = EventSystem.current;
        var module = es != null && es.currentInputModule != null ? es.currentInputModule.ToString() : "(none)";
        hooks.Log?.Invoke($"[StickyCursor] EventSystem input module: {module}");
    }

    // The ticker dies with its canvas (scene change / teardown): release now so the cursor and input are never stranded.
    private void OnDestroy()
    {
        try { Sticky?.Service.OnHostLost(); }
        catch { /* teardown best-effort */ }
    }
}
