using System;
using Stellar.Abstractions.Services;

namespace Stellar.Host;

/// <summary>
/// One-shot PRE-WARM of a lazily-built framework window (WindowBuilder-Patterns.md "PRE-WARM a lazily-built window",
/// same recipe as the layout toolbar's PrewarmToolbar): mount it once in the background so its first real open only
/// un-parks it instead of building the whole tree inside that frame.
/// <para>The window's spec must AND <c>!Warming</c> into its <c>ShouldRender</c>: <c>ShouldRender</c> only gates
/// post-mount ACTIVATION, not the mount, so while warming the tree is built but never activated — it never flashes,
/// never takes raycasts or focus, in any game phase. After <see cref="MountTicks"/> ticks it is parked (Visible=false),
/// exactly like a never-opened window. Never fronted: Z-order is left as for a never-opened window.</para>
/// <para>A real open mid-warm-up must call <see cref="EndForRealOpen"/> BEFORE its <c>SetVisible(true)</c>.</para>
/// </summary>
internal sealed class WindowPrewarm
{
    // A few ticks so the mount (+ its deferred layout apply) lands whatever the tick ordering. The mount's first Apply
    // also builds the Conditionals whose When doesn't depend on visibility (the active tab / mode).
    private const int MountTicks = 3;

    private readonly string _windowId;
    private int _phase;   // 0 = pending, 1 = mounted and waiting MountTicks, 2 = done (never runs again)
    private int _ticks;

    public WindowPrewarm(string windowId) { _windowId = windowId; }

    /// <summary>True only during the warm-up mount: the window is Visible to the window service (so it mounts) but
    /// must not be shown — AND <c>!Warming</c> into ShouldRender and into any "is it on screen" check.</summary>
    public bool Warming { get; private set; }

    /// <summary>Finished, skipped, or taken over by a real open — the next window's warm-up may start.</summary>
    public bool Settled => _phase == 2;

    /// <summary>Pump once per tick, right AFTER WindowService.Tick. <paramref name="ready"/> = the environment gate
    /// (canvas scale settled, not loading, language known); retried every tick until true.</summary>
    public void Tick(IWindowControl? control, Func<bool> ready)
    {
        if (_phase == 2 || control == null) return;
        if (_phase == 0)
        {
            // STELLAR_AUTO_OPEN may already have shown it by plain SetVisible(true); a warm-up would then hide it.
            // Same for a window the user opened before the warm-up started: already built, nothing to warm.
            var autoOpen = Environment.GetEnvironmentVariable("STELLAR_AUTO_OPEN");
            if ((autoOpen != null && autoOpen.Contains(_windowId)) || control.IsShown) { _phase = 2; return; }
            if (!ready()) return;
            Warming = true;               // ShouldRender false → mounts (builds the tree) but never activates
            control.SetVisible(true);     // Visible=true → the next WindowService.Tick mounts it
            _ticks = 0;
            _phase = 1;
            return;
        }
        if (++_ticks < MountTicks) return;
        control.SetVisible(false);        // park: tree kept, deactivated, reused by the real open
        Warming = false;
        _phase = 2;
    }

    /// <summary>A real open during the warm-up takes over: drop the suppression and finish the one-shot BEFORE the
    /// caller's SetVisible(true), and repaint now (the window may already be mounted and suppressed — without MarkDirty
    /// it would wait ≤100 ms for the next capped apply). No-op once settled.</summary>
    public void EndForRealOpen(IWindowControl? control)
    {
        if (_phase == 2) return;
        var wasMounting = _phase == 1;
        Warming = false;
        _phase = 2;
        if (wasMounting) control?.MarkDirty();
    }
}
