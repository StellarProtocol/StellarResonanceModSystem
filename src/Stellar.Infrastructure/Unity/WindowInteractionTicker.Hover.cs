using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// Hover cells + brand-logo pulses of the window interaction ticker.
//
// The hover pass is GATED (HoverScanGate): with the mouse at rest and nothing moving it no longer hit-tests every cell
// every rendered frame (raid profile 2026-11-10: 2.9% of the main thread, ~1.2 ms/frame, 1.1% of it Component.gameObject).
// Dirty signals that can move content under a still pointer, fed below + by WindowRenderer via MarkUiGeometryDirty:
//   ticker-side  — registered hover cells / window roots added or removed, the open dropdown popup changed, layout edit
//                  mode flipped, a window drag / resize / reorder / slot drag in progress, any list content moved
//                  (user OR programmatic — TickScrollActivity sets _contentMovedThisFrame), the UI scale changed;
//   input        — any key / mouse button down, a left/right release (uGUI clicks fire on release) or a wheel tick opens
//                  a short every-frame window (a click changes content through the plugin's next apply, not this frame);
//   renderer     — mount / destroy / park / show / SetRect / z-order reorder (WindowRenderer → MarkUiGeometryDirty).
// Anything else (a list growing from live data under a still pointer, a ConditionalElement switching) is caught by the
// gate's IdleRescanSec safety net. Pulses are NOT gated — they animate every frame.
public sealed partial class WindowInteractionTicker
{
    // Per hover cell, parallel to Hovers (pruned together): current state + the cell's GameObject, fetched once instead of
    // a Component.gameObject interop call per cell per scan. A destroyed cell's cached GameObject reads == null too
    // (a RectTransform can't be destroyed without its GameObject), and Prune drops the entry on window destroy.
    private struct HoverSlot { public bool On; public GameObject? Go; }
    private readonly List<HoverSlot> _hoverSlots = new();

    private readonly HoverScanGate _hoverGate = new();
    private int _hoverSeenCount = -1, _rootsSeenCount = -1;
    private System.IntPtr _popupSeen;
    private bool _editSeen;
    private bool _contentMovedThisFrame;   // set by TickScrollActivity (.PixelPerfect.cs), consumed here

    /// <summary>Something that can move or reveal content under a still pointer happened (window mount / destroy / show /
    /// hide / SetRect / z-order): run the hover pass this frame and the next. Called by WindowRenderer.</summary>
    internal void MarkUiGeometryDirty() => _hoverGate.MarkDirty();

    // Tile hover (icon grow + brighten) + brand-logo glow pulse. Hover polls the pointer against each registered
    // cell (skipping the launcher's pool of hidden tiles); fires on change only.
    private void TickHoversAndPulses()
    {
        var hp = Input.mousePosition;
        var popup = ModalPopupLayer?.Invoke();   // open dropdown → modal for hover (HoverBlocked, .HitTest.cs)
        if (HoverScanDue(hp, popup)) ScanHovers(hp, popup);

        if (Pulses.Count > 0)
        {
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * PulseSpeed);
            for (var i = 0; i < Pulses.Count; i++) { try { Pulses[i](pulse); } catch { } }
        }
    }

    // Feed this frame's dirty signals to the gate, then ask it. All checks are managed field reads except the input polls
    // (a handful of interop calls per frame, vs several per registered cell for a scan).
    private bool HoverScanDue(Vector3 hp, Transform? popup)
    {
        var now = Time.unscaledTime;
        // Popup compared by native pointer: interop wrappers for the same Transform aren't guaranteed to be one object.
        var popupPtr = popup != null ? popup.Pointer : System.IntPtr.Zero;
        var editing = LayoutEditGate.IsEditing;
        if (Hovers.Count != _hoverSeenCount || WindowRoots.Count != _rootsSeenCount || popupPtr != _popupSeen
            || editing != _editSeen || _contentMovedThisFrame
            || _activeWinDrag != null || _activeResize >= 0 || _activeSlotDrag >= 0 || _rl != null)
        {
            _hoverSeenCount = Hovers.Count; _rootsSeenCount = WindowRoots.Count; _popupSeen = popupPtr; _editSeen = editing;
            _hoverGate.MarkDirty();
        }
        _contentMovedThisFrame = false;
        var wheel = Input.mouseScrollDelta;
        if (Input.anyKeyDown || Input.GetMouseButtonUp(0) || Input.GetMouseButtonUp(1) || wheel.x != 0f || wheel.y != 0f)
            _hoverGate.MarkInput(now);
        return _hoverGate.ShouldScan(hp.x, hp.y, now);
    }

    private void ScanHovers(Vector3 hp, Transform? popup)
    {
        for (var i = 0; i < Hovers.Count; i++)
        {
            while (_hoverSlots.Count <= i) _hoverSlots.Add(default);
            var (cell, set) = Hovers[i];
            var slot = _hoverSlots[i];
            if (slot.Go is null)
            {
                if (cell == null) continue;
                slot.Go = cell.gameObject;   // once per cell (cached)
                _hoverSlots[i] = slot;
            }
            var go = slot.Go;
            if (go == null) continue;   // destroyed, not pruned yet
            if (!go.activeInHierarchy) { if (slot.On) { slot.On = false; _hoverSlots[i] = slot; try { set(false); } catch { } } continue; }
            var over = RectTransformUtility.RectangleContainsScreenPoint(cell, hp, null)
                       && !OutsideClip(cell, hp)   // the scrolled-off (clipped) part of a row is not hoverable
                       && !HoverBlocked(hp, cell, popup);   // FrontWindowBlocks, or the open popup owns the pointer
            if (over != slot.On) { slot.On = over; _hoverSlots[i] = slot; try { set(over); } catch { } }
        }
    }

    // Prune() helper: drop the parallel slot with its Hovers entry.
    private void RemoveHoverAt(int i)
    {
        Hovers.RemoveAt(i);
        if (i < _hoverSlots.Count) _hoverSlots.RemoveAt(i);
    }
}
