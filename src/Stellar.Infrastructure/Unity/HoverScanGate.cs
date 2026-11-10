namespace Stellar.Infrastructure.Unity;

/// <summary>Decides, per rendered frame, whether the ticker's hover hit-test pass must run (WindowInteractionTicker.Hover.cs).
/// The pass costs a few IL2CPP calls per registered cell (hundreds of cells: the launcher tile pool, list rows), and in a
/// raid profile it was 2.9% of the main thread (~1.2 ms/frame) with the mouse resting. Hover only changes when the pointer
/// moves OR content moves under a still pointer, so the pass runs when:
/// <list type="bullet">
/// <item>the mouse moved (any amount);</item>
/// <item>something marked the UI geometry dirty (<see cref="MarkDirty"/>: window mount/destroy/show/hide/move/resize,
/// z-order change, list content moved, a dropdown opened/closed, layout edit mode flipped, a drag in progress) — the pass
/// runs on that frame AND the next, because a change made during this frame is laid out by uGUI only at the end of it;</item>
/// <item>within <see cref="AfterInputSec"/> of any key / mouse-button / wheel input (<see cref="MarkInput"/>): a click or
/// a tab hotkey changes content through the plugin's next window apply (~10 Hz), not this frame;</item>
/// <item>at least every <see cref="IdleRescanSec"/> regardless — the safety net for content that changes under a still
/// pointer with no signal (a list growing from live data, a ConditionalElement branch switching, text re-wrapping).</item>
/// </list>
/// Pure (no Unity types) so the rule is unit-tested; the ticker feeds it mouse position and Time.unscaledTime.</summary>
internal sealed class HoverScanGate
{
    /// <summary>Longest a still pointer goes without a hover pass (the no-signal safety net).</summary>
    public const float IdleRescanSec = 0.25f;
    /// <summary>After any input, scan every frame this long (covers the click → next apply → layout delay).</summary>
    public const float AfterInputSec = 0.3f;

    private bool _primed;
    private float _x, _y;
    private float _lastScan;
    private float _inputUntil = float.NegativeInfinity;
    private int _dirtyFrames;

    /// <summary>Geometry under the pointer may have changed: scan this frame and the next.</summary>
    public void MarkDirty() => _dirtyFrames = 2;

    /// <summary>Key / button / wheel input at <paramref name="now"/>: scan every frame for <see cref="AfterInputSec"/>.</summary>
    public void MarkInput(float now) => _inputUntil = now + AfterInputSec;

    /// <summary>True when the hover pass must run this frame; records the scan (mouse + time) when it says yes.</summary>
    public bool ShouldScan(float mouseX, float mouseY, float now)
    {
        var scan = !_primed || mouseX != _x || mouseY != _y || _dirtyFrames > 0 || now < _inputUntil
                   || now - _lastScan >= IdleRescanSec || now < _lastScan;   // clock went backwards: rescan, re-anchor
        if (!scan) return false;
        _primed = true; _x = mouseX; _y = mouseY; _lastScan = now;
        if (_dirtyFrames > 0) _dirtyFrames--;
        return true;
    }
}
