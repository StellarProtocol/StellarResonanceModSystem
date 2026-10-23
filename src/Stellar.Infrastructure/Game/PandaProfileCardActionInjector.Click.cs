using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Manual click detection for <see cref="PandaProfileCardActionInjector"/>'s injected buttons, with
/// Unity/ZButton semantics: a click fires on RELEASE over the SAME button it was pressed on, and is
/// cancelled if the press turned into a drag (pointer travel past the drag threshold, or the action-bar
/// <c>ZScrollRect</c> content moving under it). The bar is drag-scrollable, so firing on the press edge
/// (the old behaviour) triggered an action whenever the user merely grabbed the bar on one of our
/// buttons to scroll it.
/// </summary>
internal sealed partial class PandaProfileCardActionInjector
{
    // Minimum pointer travel (px) that turns a press into a drag. EventSystem.pixelDragThreshold is used
    // when larger; Unity doesn't scale it for DPI, so this floor keeps high-res screens from cancelling on
    // ordinary hand jitter.
    private const float MinDragThresholdPx = 10f;
    // Content anchoredPosition delta (canvas units) that means the ScrollRect started dragging / is coasting
    // on inertia. Small enough to catch a real scroll, large enough to ignore float noise from layout passes.
    private const float ContentMoveThreshold = 2f;

    private bool _wasMouseDown;
    // The scroll content (layout_interactive = the injected buttons' parent), cached at inject time so the
    // drag check can compare its anchoredPosition against the press snapshot without a per-tick Find.
    private RectTransform? _content;
    // The pending press: the button pressed on (null = none), where the press landed, and the content's
    // scroll offset at that moment. Held as the entry reference (not an index) so a re-inject can't alias it.
    private InjectedAction? _pressed;
    private Vector2 _pressScreenPos;
    private Vector2 _pressContentPos;

    // Edge-track the HELD state ourselves (GetMouseButton level) rather than GetMouseButtonDown/Up: this
    // Tick runs on the Panda Game.Update loop, which doesn't align with the Unity frame where the edge flag
    // is true, so GetMouseButtonDown was missed on most clicks (the "needs 10-15 clicks" symptom).
    private void DetectManualClick()
    {
        if (_injected.Count == 0) { ResetClickState(); return; }

        var down = Input.GetMouseButton(0);
        var wasDown = _wasMouseDown;
        _wasMouseDown = down;

        if (down && !wasDown) BeginPress();
        else if (down) CancelPressIfDragged();
        else if (wasDown) EndPress();
    }

    // Card closed / re-injected / no buttons: drop any pending press. (_content is re-cached per inject.)
    private void ResetClickState()
    {
        _wasMouseDown = false;
        _pressed = null;
    }

    // Press edge: record which injected button (if any) is under the pointer, plus the drag baselines.
    private void BeginPress()
    {
        _pressed = HitTestInjected(Input.mousePosition);
        if (_pressed == null) return;
        _pressScreenPos = Input.mousePosition;
        _pressContentPos = _content != null ? _content.anchoredPosition : Vector2.zero;
    }

    // While held: a pointer move past the drag threshold, or the scroll content moving (ScrollRect drag or
    // inertia), turns the press into a drag — cancel it so the release can't fire. Also drop it if the
    // pressed button died.
    private void CancelPressIfDragged()
    {
        if (_pressed == null || PressSurvives(_pressed)) return;
        _pressed = null;
    }

    // Release edge: fire only if the press survived AND the pointer is still inside the SAME button. The
    // drag checks re-run here because a release can arrive with no intervening held tick (fast click, or
    // the Panda tick cadence skipping frames), so the release pose itself must also be under the threshold.
    private void EndPress()
    {
        var pressed = _pressed;
        _pressed = null;
        if (pressed == null || !PressSurvives(pressed)) return;
        if (!ContainsPointer(pressed, Input.mousePosition)) return;   // released off the button — no click
        InvokeAction(pressed.Spec);
    }

    // False if the pressed button died, the pointer travelled past the drag threshold, or the scroll content
    // moved since the press (ScrollRect drag / inertia). Cancellations are diag-logged with the reason.
    private bool PressSurvives(InjectedAction pressed)
    {
        if (!IsAlive(pressed)) return false;

        var moved = ((Vector2)Input.mousePosition - _pressScreenPos).magnitude;
        if (moved > DragThresholdPx()) { DiagPressCancelled(pressed.Spec.Id, "pointer drag", moved); return false; }

        if (_content == null) return true;
        var scrolled = (_content.anchoredPosition - _pressContentPos).magnitude;
        if (scrolled > ContentMoveThreshold) { DiagPressCancelled(pressed.Spec.Id, "content scrolled", scrolled); return false; }
        return true;
    }

    // The single injected button under the pointer (first match wins — a press resolves to one button).
    private InjectedAction? HitTestInjected(Vector3 screenPos)
    {
        for (var i = 0; i < _injected.Count; i++)
        {
            var hit = _injected[i];
            if (IsAlive(hit) && ContainsPointer(hit, screenPos)) return hit;
        }
        return null;
    }

    private static bool IsAlive(InjectedAction a) => a.Go != null && a.Rt != null && a.Go.activeInHierarchy;

    private static bool ContainsPointer(InjectedAction a, Vector3 screenPos) =>
        RectTransformUtility.RectangleContainsScreenPoint(a.Rt, screenPos, ResolveCanvasCamera(a.Go));

    private static float DragThresholdPx()
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        return es != null ? Math.Max(es.pixelDragThreshold, MinDragThresholdPx) : MinDragThresholdPx;
    }

    // The camera RectangleContainsScreenPoint needs for a Camera/World-space canvas; null for a
    // ScreenSpaceOverlay canvas. Resolve from the ROOT canvas, not the nearest one: a nested sub-canvas can
    // report a null/wrong worldCamera while the rootCanvas carries the correct one.
    private static Camera? ResolveCanvasCamera(GameObject go)
    {
        var canvas = go.GetComponentInParent<Canvas>()?.rootCanvas;
        return canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
    }

    private void InvokeAction(ProfileCardActionSpec spec)
    {
        try
        {
            var charId = ReadOpenCardCharId();
            if (charId <= 0) return;   // unreadable open-card charId — gated DiagCardIdReadFailed already noted it
            DiagHitTestClick(spec.Id, charId);
            spec.OnClick(new EntityId((charId << 16) | 640L));
        }
        catch (Exception ex)
        {
            _log.Error($"[IdCard] action '{spec.Id}' click threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
