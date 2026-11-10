using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// Hover cells + brand-logo pulses of the window interaction ticker (split out of WindowInteractionTicker.cs).
public sealed partial class WindowInteractionTicker
{
    // Tile hover (icon grow + brighten) + brand-logo glow pulse. Hover polls the pointer against each registered
    // cell (skipping the launcher's pool of hidden tiles); fires on change only.
    private void TickHoversAndPulses()
    {
        var hp = Input.mousePosition;
        var popup = ModalPopupLayer?.Invoke();   // open dropdown → modal for hover (HoverBlocked, .HitTest.cs)
        for (var i = 0; i < Hovers.Count; i++)
        {
            while (_hoverState.Count <= i) _hoverState.Add(false);
            var (cell, set) = Hovers[i];
            if (cell == null) continue;
            if (!cell.gameObject.activeInHierarchy) { if (_hoverState[i]) { _hoverState[i] = false; try { set(false); } catch { } } continue; }
            var over = RectTransformUtility.RectangleContainsScreenPoint(cell, hp, null)
                       && !OutsideClip(cell, hp)   // the scrolled-off (clipped) part of a row is not hoverable
                       && !HoverBlocked(hp, cell, popup);   // FrontWindowBlocks, or the open popup owns the pointer
            if (over != _hoverState[i]) { _hoverState[i] = over; try { set(over); } catch { } }
        }

        if (Pulses.Count > 0)
        {
            var pulse = 0.5f + 0.5f * Mathf.Sin(Time.realtimeSinceStartup * PulseSpeed);
            for (var i = 0; i < Pulses.Count; i++) { try { Pulses[i](pulse); } catch { } }
        }
    }
}
