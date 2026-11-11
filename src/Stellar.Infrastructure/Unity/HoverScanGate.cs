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
/// Before any of that: while the game has the cursor hidden / locked (camera mode, see <see cref="CursorHidden"/>) no mod
/// window can be hovered, so the pass does not run at all — in combat the skill keys alone would otherwise hold the
/// after-input window open and scan nearly every frame.
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
    private bool _cursorWasHidden;

    /// <summary>Passes run / skipped (still pointer) / skipped (cursor hidden) since the last <see cref="TakeCounts"/>.
    /// Plain int increments, always on; only the diagnostics-gated [UiHover] line reads them.</summary>
    public int Scans { get; private set; }
    public int SkipsIdle { get; private set; }
    public int SkipsHidden { get; private set; }

    /// <summary>Read and reset the counters (the [UiHover] summary window).</summary>
    public (int Scans, int Idle, int Hidden) TakeCounts()
    {
        var c = (Scans, SkipsIdle, SkipsHidden);
        Scans = SkipsIdle = SkipsHidden = 0;
        return c;
    }

    /// <summary>Geometry under the pointer may have changed: scan this frame and the next.</summary>
    public void MarkDirty() => _dirtyFrames = 2;

    /// <summary>The cursor is hidden or locked this frame (the game's camera mode), so nothing can be hovered: the pass is
    /// skipped. Returns true on the FIRST such frame only — the caller then turns any lit cell off once, so nothing stays
    /// highlighted under an invisible cursor. The first usable frame afterwards always scans (see <see cref="ShouldScan"/>):
    /// the pointer reappears wherever the game put it, and lists may have moved meanwhile.</summary>
    public bool CursorHidden()
    {
        SkipsHidden++;
        if (_cursorWasHidden) return false;
        _cursorWasHidden = true;
        return true;
    }

    /// <summary>Key / button / wheel input at <paramref name="now"/>: scan every frame for <see cref="AfterInputSec"/>.</summary>
    public void MarkInput(float now) => _inputUntil = now + AfterInputSec;

    /// <summary>True when the hover pass must run this frame; records the scan (mouse + time) when it says yes.</summary>
    public bool ShouldScan(float mouseX, float mouseY, float now)
    {
        var scan = !_primed || _cursorWasHidden || mouseX != _x || mouseY != _y || _dirtyFrames > 0 || now < _inputUntil
                   || now - _lastScan >= IdleRescanSec || now < _lastScan;   // clock went backwards: rescan, re-anchor
        if (!scan) { SkipsIdle++; return false; }
        Scans++;
        _primed = true; _cursorWasHidden = false; _x = mouseX; _y = mouseY; _lastScan = now;
        if (_dirtyFrames > 0) _dirtyFrames--;
        return true;
    }
}
