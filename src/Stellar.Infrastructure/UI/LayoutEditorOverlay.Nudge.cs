using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// Arrow-key NUDGE of the selected layout element, active only in edit mode (pumped from
/// <see cref="TickInput"/>, inside the <c>IsEditing</c> branch, AFTER ProcessInput and BEFORE SyncChrome so the
/// outline tracks the nudged rect the same tick). A single tap moves 1&#160;px (fine); holding accelerates from a
/// slow start up to a capped max speed (OS-key-repeat feel). No-op outside edit mode, with nothing selected, while
/// a mouse drag is in progress, while a text field has focus, or while Alt is held (Alt+E toggles edit mode).
/// </summary>
/// <remarks>
/// Uses WALL-CLOCK time (<see cref="Time.unscaledTime"/>), NOT <c>unscaledDeltaTime</c>: TickInput runs at the
/// throttled framework tick rate (not per-frame), so a per-frame delta would understate the time elapsed between
/// ticks and the ramp would crawl. Coordinate convention is top-left origin, Y-DOWN (screen px; matches
/// LayoutEditorService.SnapToScreenEdges) → UpArrow DECREASES Y, DownArrow increases it.
/// </remarks>
internal sealed partial class LayoutEditorOverlay
{
    // Ramp tuning — baked code defaults (Session Rule 12: manual settings, NOT auto-learned).
    private const float InitialRepeatDelay = 0.25f;   // seconds held before continuous movement begins
    private const float NudgeBaseSpeed     = 140f;    // px/sec at continuous-start
    private const float NudgeMaxSpeed      = 1100f;   // px/sec hard cap (the speed limit)
    private const float NudgeRampDuration  = 1.3f;    // seconds from continuous-start to reach MaxSpeed
    private const float NudgeMinOnScreenPx = 24f;     // keep at least this many px of a native element on-screen

    private float _nudgeHeldSeconds;
    private Vector2 _nudgeAccum;         // fractional-pixel carry (slow speeds still move accurately over ticks)
    private float _nudgeLastTime;
    private bool _nativeNudgePendingCommit;   // a native-UI nudge moved the rect in-memory; persist on key-release
    private string? _nativeNudgePendingId;

    // Arrow-key nudge of the currently-selected element. Called each tick while editing (after ProcessInput).
    private void TickNudge()
    {
        // Gate: must have a selection, must not fight a mouse drag, must not hijack typing, keep Alt clear (Alt+E).
        if (_editor.SelectedWindowId is not { } id
            || _editor.IsDragging
            || (_windows?.AnyFieldFocused ?? false)
            || (_input.CurrentModifiers & ModifierKeys.Alt) != 0)
        {
            ResetNudge();
            return;
        }

        float now = Time.unscaledTime;
        float dt = Mathf.Clamp(now - _nudgeLastTime, 0f, 0.1f);   // guard first call / long gaps
        _nudgeLastTime = now;

        // Per-axis direction (allows diagonal). Y-down: Down = +1, Up = -1.
        int dx = (_input.IsKeyHeld(StellarKeyCode.RightArrow) ? 1 : 0) + (_input.IsKeyHeld(StellarKeyCode.LeftArrow) ? -1 : 0);
        int dy = (_input.IsKeyHeld(StellarKeyCode.DownArrow) ? 1 : 0) + (_input.IsKeyHeld(StellarKeyCode.UpArrow) ? -1 : 0);

        if (dx == 0 && dy == 0)
        {
            ResetNudge();   // nothing held: reset ramp/accum and commit any pending native move
            return;
        }

        // Down-edge of ANY arrow = immediate 1px fine tap in the current direction; restart the ramp clock.
        if (ArrowPressedThisFrame())
        {
            ApplyNudge(id, dx == 0 ? 0f : Mathf.Sign(dx), dy == 0 ? 0f : Mathf.Sign(dy));
            _nudgeHeldSeconds = 0f;
        }

        // Continuous movement only after the initial repeat delay, then ramp BaseSpeed → MaxSpeed over RampDuration.
        _nudgeHeldSeconds += dt;
        if (_nudgeHeldSeconds >= InitialRepeatDelay)
        {
            float t = Mathf.Clamp01((_nudgeHeldSeconds - InitialRepeatDelay) / NudgeRampDuration);
            float speed = Mathf.Lerp(NudgeBaseSpeed, NudgeMaxSpeed, t);
            _nudgeAccum += new Vector2(dx, dy).normalized * speed * dt;
            // Take the integer part (truncate toward zero) as this tick's px delta; keep the fractional remainder.
            float stepX = (int)_nudgeAccum.x;
            float stepY = (int)_nudgeAccum.y;
            if (stepX != 0f || stepY != 0f)
            {
                _nudgeAccum = new Vector2(_nudgeAccum.x - stepX, _nudgeAccum.y - stepY);
                ApplyNudge(id, stepX, stepY);
            }
        }
    }

    // Reset the ramp when no arrow is held (or a gate blocks nudging). Commits a pending native-UI move once here,
    // on release — mirrors the mouse-drag release (HandleDragRelease → _nativeUi.Commit), so we never disk-write a
    // native pose every tick. Cheap + allocation-free when nothing is pending.
    private void ResetNudge()
    {
        _nudgeHeldSeconds = 0f;
        _nudgeAccum = Vector2.zero;
        _nudgeLastTime = Time.unscaledTime;
        if (_nativeNudgePendingCommit)
        {
            if (_nativeNudgePendingId != null) _nativeUi?.Commit(_nativeNudgePendingId);
            _nativeNudgePendingCommit = false;
            _nativeNudgePendingId = null;
        }
    }

    private bool ArrowPressedThisFrame()
    {
        var pressed = _input.PressedKeysThisFrame;
        for (int i = 0; i < pressed.Count; i++)
        {
            var k = pressed[i];
            if (k == StellarKeyCode.UpArrow || k == StellarKeyCode.DownArrow
                || k == StellarKeyCode.LeftArrow || k == StellarKeyCode.RightArrow) return true;
        }
        return false;
    }

    // Apply a screen-px delta to the selected element, dispatching by type. A resolved native entry with this id →
    // native path (screen px directly, clamp on-screen, persist on release). Otherwise → mod-window path, which
    // converts to canvas units and persists via PersistIfSettled (see WindowService.NudgeRect).
    private void ApplyNudge(string id, float dxPx, float dyPx)
    {
        if (dxPx == 0f && dyPx == 0f) return;

        if (_nativeUi != null)
        {
            foreach (var e in _nativeUi.Entries)
            {
                if (!e.IsResolved || e.Descriptor.Id != id) continue;
                var rect = _nativeUi.GetLiveRect(e);
                var res = _input.CurrentResolution;
                // Keep ≥ NudgeMinOnScreenPx of the element within the screen (top-left origin, Y-down).
                float nx = Mathf.Clamp(rect.X + dxPx, NudgeMinOnScreenPx - rect.Width, res.Width - NudgeMinOnScreenPx);
                float ny = Mathf.Clamp(rect.Y + dyPx, NudgeMinOnScreenPx - rect.Height, res.Height - NudgeMinOnScreenPx);
                _nativeUi.SetRect(id, new WindowRect(nx, ny, rect.Width, rect.Height));   // in-memory; Commit on release
                _nativeNudgePendingCommit = true;
                _nativeNudgePendingId = id;
                return;
            }
        }

        _windows?.NudgeRect(id, dxPx, dyPx);   // canvas-unit convert + clamp + persist-on-settle live here
    }
}
