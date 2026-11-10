using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

/// <summary>
/// PRE-WARM of a lazily-built framework window (WindowBuilder-Patterns.md "PRE-WARM a lazily-built window", same recipe
/// as the layout toolbar's PrewarmToolbar): mount it once in the background so its first real open only un-parks it
/// instead of building the whole tree inside that frame.
/// <para>The window's spec must AND <c>!Warming</c> into its <c>ShouldRender</c>: <c>ShouldRender</c> only gates
/// post-mount ACTIVATION, not the mount, so while warming the tree is built but never activated — it never flashes,
/// never takes raycasts or focus, in any game phase. Never fronted: Z-order is left as for a never-opened window.</para>
/// <para>BACKGROUND WARM (renderer IWindowBackgroundWarm): the mount lands off-screen and the window then stays active
/// there, applied like a shown window, until the renderer reports its background work done (time-sliced tab builds laid
/// out and parked, bubbles settled) — Unity lays out nothing under an inactive root, so a window parked right after its
/// mount paid all of that just after its first open (WindowRenderer.BackgroundWarm.cs). Then it is parked (Visible=false),
/// exactly like a never-opened window. Without a renderer capability it parks after <see cref="MountTicks"/> as before.</para>
/// <para>RE-WARM on a content change (<see cref="WatchContent"/>): the launcher is warmed at the title screen, but what it
/// shows changes after world entry (world-only plugin tiles pass their ShouldShow, counts change), and its first open in
/// the world then rebuilt the tile grid in one apply (171.7 ms frame, 42 bubble measures + a structural re-layout, 11-09).
/// So while it is hidden a cheap content signature is sampled; when it moved, the same background warm runs again
/// (off-screen, bubble measures budgeted) and the next open is a plain re-show again.</para>
/// <para>A real open mid-warm-up must call <see cref="EndForRealOpen"/> BEFORE its <c>SetVisible(true)</c>.</para>
/// </summary>
internal sealed class WindowPrewarm
{
    // A few ticks so the mount (+ its deferred layout apply) lands whatever the tick ordering. The mount's first Apply
    // also builds the Conditionals whose When doesn't depend on visibility (the active tab / mode).
    private const int MountTicks = 3;
    // Ticks (pumped every global tick, ~a frame) a background warm may take before the window is parked anyway — the
    // hub's normally takes 1-4 s (bubble measures are budgeted to 3 ms per 10 Hz apply).
    private const int MaxWarmTicks = 1800;
    private const int WatchEveryTicks = 30;    // content signature sample while idle (~0.5 s)
    private const int RewarmGapTicks = 120;    // ≥ ~2 s from the end of one warm to the start of the next (churn guard)

    private readonly string _windowId;
    private readonly ContentWatch _watch = new();
    private int _phase;   // 0 = first warm pending, 1 = warming, 2 = idle (watching content), 3 = re-warm pending
    private int _ticks;   // phase 1: ticks into the warm; phases 2-3: ticks since the last warm ended
    private int _rewarms;
    private IWindowBackgroundWarm? _warm;
    private Func<int>? _signature;
    private Func<string>? _describe;
    private Action<string>? _log;
    private string _warmedWith = "";   // _describe() of the content the last warm (or the last shown sample) applied

    public WindowPrewarm(string windowId) { _windowId = windowId; }

    /// <summary>True only during a warm-up mount: the window is Visible to the window service (so it mounts) but
    /// must not be shown — AND <c>!Warming</c> into ShouldRender and into any "is it on screen" check.</summary>
    public bool Warming { get; private set; }

    /// <summary>The FIRST warm finished, was skipped, or was taken over by a real open — the next window's warm-up may
    /// start. Re-warms never clear it.</summary>
    public bool Settled { get; private set; }

    /// <summary>Re-warm the window whenever <paramref name="signature"/> (a cheap hash of everything it shows) changes
    /// while it is hidden. <paramref name="describe"/> + <paramref name="log"/>: the "[Prebuild] &lt;id&gt;: background
    /// re-warm" line (reason = the content before → after).</summary>
    public void WatchContent(Func<int> signature, Func<string> describe, Action<string> log)
    {
        _signature = signature;
        _describe = describe;
        _log = log;
    }

    /// <summary>Pump once per tick, right AFTER WindowService.Tick. <paramref name="ready"/> = the environment gate
    /// (canvas scale settled, not loading, language known); retried every tick until true. <paramref name="warm"/> = the
    /// renderer's background-warm capability (null = plain mount-and-park).</summary>
    public void Tick(IWindowControl? control, Func<bool> ready, IWindowBackgroundWarm? warm)
    {
        if (control == null) return;
        switch (_phase)
        {
            case 0: TickFirst(control, ready, warm); break;
            case 1: TickWarming(control); break;
            case 2: TickWatch(control); break;
            default: TickRewarmPending(control, ready, warm); break;
        }
    }

    private void TickFirst(IWindowControl control, Func<bool> ready, IWindowBackgroundWarm? warm)
    {
        // STELLAR_AUTO_OPEN may already have shown it by plain SetVisible(true); a warm-up would then hide it.
        // Same for a window the user opened before the warm-up started: already built, nothing to warm.
        var autoOpen = Environment.GetEnvironmentVariable("STELLAR_AUTO_OPEN");
        if ((autoOpen != null && autoOpen.Contains(_windowId)) || control.IsShown) { Idle(); return; }
        if (!ready()) return;
        Begin(control, warm);
    }

    private void Begin(IWindowControl control, IWindowBackgroundWarm? warm)
    {
        Warming = true;               // ShouldRender false → mounts (builds the tree) but never activates on screen
        _warm = warm;
        _warm?.SetBackgroundWarm(_windowId, true);   // before the mount / un-park: it lands off-screen, stays active there
        control.SetVisible(true);     // Visible=true → the next WindowService.Tick mounts (or un-parks) it
        _ticks = 0;
        _phase = 1;
    }

    private void TickWarming(IWindowControl control)
    {
        var done = _warm?.IsBackgroundWarmDone(_windowId) ?? true;
        if (!BackgroundWarmRules.ShouldPark(++_ticks, MountTicks, MaxWarmTicks, done)) return;
        _warm?.SetBackgroundWarm(_windowId, false);
        control.SetVisible(false);        // park: tree kept, deactivated (and moved home), reused by the real open
        Warming = false;
        Idle();
        if (_signature != null) { _watch.Reset(Sample()); _warmedWith = Describe(); }   // what this warm applied
    }

    private void Idle() { _phase = 2; _ticks = 0; Settled = true; }

    // Shown: its bindings keep it current → new baseline. Hidden and the signature moved → a re-warm is due.
    private void TickWatch(IWindowControl control)
    {
        if (_signature == null || ++_ticks % WatchEveryTicks != 0) return;
        var shown = control.IsShown;
        var sig = Sample();
        if (shown && sig != _watch.Baseline) _warmedWith = Describe();
        if (_watch.Changed(sig, shown)) _phase = 3;
    }

    // Waits for the churn gap and the environment gate (canvas ready; a loading screen only with the world up under it —
    // the world-entry content change re-warms behind it, Wiring.Prewarm.cs CanStartPrewarm). Opened meanwhile: its
    // bindings pull the change on that open (as before) — nothing left to warm.
    private void TickRewarmPending(IWindowControl control, Func<bool> ready, IWindowBackgroundWarm? warm)
    {
        if (control.IsShown) { _phase = 2; return; }
        if (++_ticks < RewarmGapTicks || !ready()) return;
        var now = Describe();
        _log?.Invoke($"[Prebuild] {_windowId}: background re-warm #{++_rewarms} — content changed while hidden ({_warmedWith} → {now})");
        _warmedWith = now;
        Begin(control, warm);
    }

    // Panel/plugin code (ShouldShow, TitleProvider): a throwing signature reads as "unchanged", never breaks the pump.
    private int Sample()
    {
        try { return _signature!(); } catch { return _watch.Baseline; }
    }

    private string Describe()
    {
        try { return _describe?.Invoke() ?? ""; } catch { return "?"; }
    }

    /// <summary>A real open during a warm-up takes over: drop the suppression and the background warm, finish the
    /// one-shot BEFORE the caller's SetVisible(true), and repaint now (the window may already be mounted off-screen —
    /// without MarkDirty it would wait ≤100 ms for the next capped apply to move it home). A pending re-warm is dropped
    /// (the open pulls the change itself). No-op while idle.</summary>
    public void EndForRealOpen(IWindowControl? control)
    {
        if (_phase == 2) return;
        var wasMounting = _phase == 1;
        Warming = false;
        Idle();
        _warm?.SetBackgroundWarm(_windowId, false);
        if (wasMounting) control?.MarkDirty();
    }
}
