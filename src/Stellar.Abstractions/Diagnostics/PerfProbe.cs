using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace Stellar.Abstractions.Diagnostics;

/// <summary>
/// Per-frame timing accumulator for the overlay perf harness. Gated by
/// <c>STELLAR_PERFHUD=1</c> (mirrors <see cref="StellarDiagnostics"/>): when
/// disabled every method early-returns on a single cached field read so
/// production pays nothing.
///
/// <para><b>Frame model.</b> <c>OnGUI</c> fires multiple times per rendered
/// Unity frame (Layout, Repaint, input events). <see cref="BeginDraw"/>/
/// <see cref="EndDraw"/> and <see cref="BeginWindow"/>/<see cref="EndWindow"/>
/// <i>accumulate</i> into the current frame's running totals across every pass;
/// <see cref="RecordFrame"/> is called exactly once per Unity frame (from the
/// Host OnGUI sink's once-per-frame block) to (a) store the frame time, (b)
/// commit the accumulated totals to the published <see cref="Snapshot"/>, (c)
/// reset the per-frame accumulators, and (d) bump <see cref="FrameCounter"/>.</para>
///
/// <para>Static mutable state is the sanctioned exception here — this is
/// diagnostic infrastructure in the same category as <see cref="StellarDiagnostics"/>,
/// not business state.</para>
/// </summary>
public static class PerfProbe
{
    private static bool _enabled = ResolveEnabled();

    /// <summary><c>true</c> when <c>STELLAR_PERFHUD=1</c> (or <c>=true</c>) at startup.</summary>
    public static bool IsEnabled => _enabled;

    private static bool ResolveEnabled()
    {
        var v = System.Environment.GetEnvironmentVariable("STELLAR_PERFHUD");
        if (string.IsNullOrEmpty(v)) return false;
        return v == "1" || v.Equals("true", System.StringComparison.OrdinalIgnoreCase);
    }

    // --- accumulators for the frame currently being built ---
    private static readonly Stopwatch _drawSw = new();
    private static double _drawMsThisFrame;
    private static readonly Dictionary<string, double> _windowMsThisFrame = new();
    private static readonly Dictionary<string, Stopwatch> _windowSw = new();
    // Update path = the mod's per-frame work on the game's Update thread
    // (plugin Update callbacks + per-tick service refreshes). Separate from the
    // OnGUI draw cost; this is where combat-event processing would show up.
    private static readonly Stopwatch _updateSw = new();
    private static double _updateMsThisFrame;

    // --- published snapshot of the last committed frame ---
    private static double _lastFrameMs;
    private static double _lastDrawMs;
    private static double _lastUpdateMs;
    private static readonly Dictionary<string, double> _publishedWindowMs = new();

    // Set true by MarkDrawFrame() when the global-rate Band 3 runs this tick; cleared inside RecordFrame
    // after the conditional window/draw publish. Ensures per-window timings only refresh on draw ticks;
    // on the faster master ticks between draws the last published values are held (republishing from the
    // empty per-frame accumulators on non-draw ticks would wipe them and freeze the overlay).
    private static bool _drewThisFrame;

    /// <summary>Monotonic Unity-frame counter; bumped by <see cref="RecordFrame"/>.</summary>
    public static int FrameCounter { get; private set; }

    // --- periodic log emission so the numbers are readable headlessly (in the
    // BepInEx log), not just on the on-screen overlay. Host sets LogSink to the
    // framework log at boot; null in tests/production-off so nothing is emitted.
    // Action<string> keeps Abstractions BCL-only. ---
    /// <summary>Optional log sink set by the host at boot; when non-null a per-interval summary line is written to the BepInEx log.</summary>
    public static System.Action<string>? LogSink;
    private const int LogIntervalFrames = 120;   // ~2s at 60fps
    private static int _intervalFrames;
    private static double _intervalFrameMsSum;
    private static double _intervalFrameMsMax;   // worst (slowest) frame in the interval
    private static double _intervalDrawMsSum;
    private static double _intervalUpdateMsSum;
    private static double _intervalUpdateAllocSum;   // bytes/frame allocated in the update path
    private static readonly Dictionary<string, double> _intervalWindowMsSum = new();

    // Managed bytes allocated on the main thread inside the update path — the GC
    // fuel that, if high, causes the single-frame stalls (worstFps) the average
    // ms can't see. Measured via GC.GetAllocatedBytesForCurrentThread() deltas.
    private static long _updateAllocStart;
    private static long _updateAllocThisFrame;
    private static long _lastUpdateAllocBytes;

    /// <summary>Mark the start of the OnGUI draw pass for the current frame.</summary>
    public static void BeginDraw()
    {
        if (!_enabled) return;
        _drawSw.Restart();
    }

    /// <summary>Mark the end of the OnGUI draw pass for the current frame; accumulated into the frame total.</summary>
    public static void EndDraw()
    {
        if (!_enabled) return;
        _drawSw.Stop();
        _drawMsThisFrame += _drawSw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Mark the start of the managed Update work for the current frame (plugins + services).</summary>
    public static void BeginUpdate()
    {
        if (!_enabled) return;
        _updateAllocStart = System.GC.GetAllocatedBytesForCurrentThread();
        _updateSw.Restart();
    }

    /// <summary>Mark the end of the managed Update work; accumulated ms and managed alloc bytes into the frame total.</summary>
    public static void EndUpdate()
    {
        if (!_enabled) return;
        _updateSw.Stop();
        _updateMsThisFrame += _updateSw.Elapsed.TotalMilliseconds;
        _updateAllocThisFrame += System.GC.GetAllocatedBytesForCurrentThread() - _updateAllocStart;
    }

    /// <summary>Marks that the global-rate draw band ran this tick. Per-window/draw timings only refresh on
    /// these frames; on the faster master ticks between draws the last published values are held (otherwise
    /// they'd be wiped to empty and the overlay would freeze). No-op unless the harness is enabled.</summary>
    public static void MarkDrawFrame() { if (_enabled) _drewThisFrame = true; }

    // --- generic named segments (to localise WITHIN the update path: which
    // plugin / service owns the per-frame cost). Accumulate per frame; the
    // [Perf] line reports the top segments by average ms. ---
    private static readonly Dictionary<string, Stopwatch> _segSw = new();
    private static readonly Dictionary<string, double> _segMsThisFrame = new();
    private static readonly Dictionary<string, double> _intervalSegMsSum = new();

    /// <summary>Start timing a named segment within the Update path (identifies per-plugin cost).</summary>
    public static void BeginSeg(string name)
    {
        if (!_enabled) return;
        if (!_segSw.TryGetValue(name, out var sw)) _segSw[name] = sw = new Stopwatch();
        sw.Restart();
    }

    /// <summary>Stop timing the named segment; accumulates into per-frame totals reported in the log summary.</summary>
    public static void EndSeg(string name)
    {
        if (!_enabled) return;
        if (!_segSw.TryGetValue(name, out var sw)) return;
        sw.Stop();
        _segMsThisFrame.TryGetValue(name, out var acc);
        _segMsThisFrame[name] = acc + sw.Elapsed.TotalMilliseconds;
    }

    // --- THREAD-SAFE hook timing. The combat/wire Harmony hooks run on the
    // network receive thread, so they can't use the (single-threaded) segment
    // dicts. Accumulate ticks/alloc/calls via Interlocked; RecordFrame (main
    // thread) drains them once per frame. This is where the in-combat per-packet
    // AOI-parsing cost shows up — invisible to the per-frame Update/draw timers. ---
    private static long _hookCombatTicks;
    private static long _hookCombatAlloc;
    private static long _hookCombatCalls;
    private static double _lastHookCombatMs;
    private static long _lastHookCombatAlloc;
    private static long _lastHookCombatCalls;
    private static double _intervalHookCombatMsSum;
    private static double _intervalHookCombatAllocSum;
    private static long _intervalHookCombatCalls;

    private static long _hookWireTicks;
    private static long _hookWireAlloc;
    private static long _hookWireCalls;
    private static double _lastHookWireMs;
    private static long _lastHookWireAlloc;
    private static long _lastHookWireCalls;
    private static double _intervalHookWireMsSum;
    private static double _intervalHookWireAllocSum;
    private static long _intervalHookWireCalls;

    /// <summary>Returns a start timestamp (ticks) for a hook, or 0 when disabled.</summary>
    public static long HookBegin() => _enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Per-thread allocated-bytes baseline for a hook (call on the hook's thread).</summary>
    public static long HookBeginAlloc() => _enabled ? System.GC.GetAllocatedBytesForCurrentThread() : 0L;

    /// <summary>Accumulate one combat-hook invocation's wall-time + this-thread allocation.</summary>
    public static void HookEndCombat(long startTicks, long startAlloc)
    {
        if (!_enabled) return;
        System.Threading.Interlocked.Add(ref _hookCombatTicks, Stopwatch.GetTimestamp() - startTicks);
        System.Threading.Interlocked.Add(ref _hookCombatAlloc, System.GC.GetAllocatedBytesForCurrentThread() - startAlloc);
        System.Threading.Interlocked.Increment(ref _hookCombatCalls);
    }

    /// <summary>Accumulate one wire-tap (TCP/UDP recv) invocation's time + this-thread allocation.</summary>
    public static void HookEndWire(long startTicks, long startAlloc)
    {
        if (!_enabled) return;
        System.Threading.Interlocked.Add(ref _hookWireTicks, Stopwatch.GetTimestamp() - startTicks);
        System.Threading.Interlocked.Add(ref _hookWireAlloc, System.GC.GetAllocatedBytesForCurrentThread() - startAlloc);
        System.Threading.Interlocked.Increment(ref _hookWireCalls);
    }

    /// <summary>Start timing an individual window's draw cost for the current frame.</summary>
    public static void BeginWindow(string id)
    {
        if (!_enabled) return;
        if (!_windowSw.TryGetValue(id, out var sw)) _windowSw[id] = sw = new Stopwatch();
        sw.Restart();
    }

    /// <summary>Stop timing the named window's draw cost; accumulated into per-frame per-window totals.</summary>
    public static void EndWindow(string id)
    {
        if (!_enabled) return;
        if (!_windowSw.TryGetValue(id, out var sw)) return;
        sw.Stop();
        _windowMsThisFrame.TryGetValue(id, out var acc);
        _windowMsThisFrame[id] = acc + sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Commit the frame being built and start a fresh one.</summary>
    public static void RecordFrame(double deltaSeconds)
    {
        if (!_enabled) return;

        _lastFrameMs = deltaSeconds * 1000.0;
        _lastUpdateMs = _updateMsThisFrame;
        _lastUpdateAllocBytes = _updateAllocThisFrame;

        // Draw-cadence data (windows + draw ms) only refreshes on frames where the global draw band ran;
        // on the faster master ticks between draws we HOLD the last published values (republishing from the
        // empty per-frame accumulators would wipe them and freeze the overlay). Update CPU / FPS stay per-tick.
        if (_drewThisFrame)
        {
            _lastDrawMs = _drawMsThisFrame;
            _publishedWindowMs.Clear();
            foreach (var kv in _windowMsThisFrame) _publishedWindowMs[kv.Key] = kv.Value;
            _drewThisFrame = false;
        }

        foreach (var kv in _segMsThisFrame)
        {
            _intervalSegMsSum.TryGetValue(kv.Key, out var s);
            _intervalSegMsSum[kv.Key] = s + kv.Value;
        }

        // Drain the thread-safe hook accumulators (filled on the network thread).
        var hookTicks = System.Threading.Interlocked.Exchange(ref _hookCombatTicks, 0L);
        _lastHookCombatAlloc = System.Threading.Interlocked.Exchange(ref _hookCombatAlloc, 0L);
        _lastHookCombatCalls = System.Threading.Interlocked.Exchange(ref _hookCombatCalls, 0L);
        _lastHookCombatMs = hookTicks * 1000.0 / Stopwatch.Frequency;

        var wireTicks = System.Threading.Interlocked.Exchange(ref _hookWireTicks, 0L);
        _lastHookWireAlloc = System.Threading.Interlocked.Exchange(ref _hookWireAlloc, 0L);
        _lastHookWireCalls = System.Threading.Interlocked.Exchange(ref _hookWireCalls, 0L);
        _lastHookWireMs = wireTicks * 1000.0 / Stopwatch.Frequency;

        _drawMsThisFrame = 0.0;
        _updateMsThisFrame = 0.0;
        _updateAllocThisFrame = 0;
        _windowMsThisFrame.Clear();
        _segMsThisFrame.Clear();
        FrameCounter++;

        AccumulateAndMaybeEmit();
    }

    // Roll the just-committed frame into the interval accumulators and, every
    // LogIntervalFrames, emit one averaged summary line to LogSink. No-op when
    // no sink is attached (tests / overlay-only use).
    private static void AccumulateAndMaybeEmit()
    {
        if (LogSink is null) return;
        _intervalFrames++;
        _intervalFrameMsSum += _lastFrameMs;
        if (_lastFrameMs > _intervalFrameMsMax) _intervalFrameMsMax = _lastFrameMs;
        _intervalDrawMsSum += _lastDrawMs;
        _intervalUpdateMsSum += _lastUpdateMs;
        _intervalUpdateAllocSum += _lastUpdateAllocBytes;
        _intervalHookCombatMsSum += _lastHookCombatMs;
        _intervalHookCombatAllocSum += _lastHookCombatAlloc;
        _intervalHookCombatCalls += _lastHookCombatCalls;
        _intervalHookWireMsSum += _lastHookWireMs;
        _intervalHookWireAllocSum += _lastHookWireAlloc;
        _intervalHookWireCalls += _lastHookWireCalls;
        foreach (var kv in _publishedWindowMs)
        {
            _intervalWindowMsSum.TryGetValue(kv.Key, out var s);
            _intervalWindowMsSum[kv.Key] = s + kv.Value;
        }
        if (_intervalFrames < LogIntervalFrames) return;
        LogSink(FormatIntervalSummary());
        ResetInterval();
    }

    private static string FormatIntervalSummary()
    {
        var n = _intervalFrames;
        var avgFrame = _intervalFrameMsSum / n;
        var avgFps = avgFrame > 0.0 ? 1000.0 / avgFrame : 0.0;
        var worstFps = _intervalFrameMsMax > 0.0 ? 1000.0 / _intervalFrameMsMax : 0.0;
        var avgDraw = _intervalDrawMsSum / n;
        var avgUpdate = _intervalUpdateMsSum / n;
        var avgUpdAllocKb = _intervalUpdateAllocSum / n / 1024.0;
        var sb = new StringBuilder();
        var avgHookMs = _intervalHookCombatMsSum / n;
        var avgHookKb = _intervalHookCombatAllocSum / n / 1024.0;
        var avgHookCalls = (double)_intervalHookCombatCalls / n;
        var avgWireMs = _intervalHookWireMsSum / n;
        var avgWireKb = _intervalHookWireAllocSum / n / 1024.0;
        var avgWireCalls = (double)_intervalHookWireCalls / n;
        sb.Append($"[Perf] n={n} avgFps={avgFps:0.0} worstFps={worstFps:0.0} avgFrame={avgFrame:0.00}ms avgUpdateCPU={avgUpdate:0.000}ms avgUpdAlloc={avgUpdAllocKb:0.0}KB avgDrawCPU={avgDraw:0.000}ms hook:combat={avgHookMs:0.000}ms/{avgHookCalls:0.0}calls/{avgHookKb:0.0}KB hook:wire={avgWireMs:0.000}ms/{avgWireCalls:0.0}calls/{avgWireKb:0.0}KB");
        sb.Append($" | hudKill={PerfControls.MasterHudKill} chromeKill={PerfControls.ChromeKill} opaque={PerfControls.ForceOpaque} throttle=1/{PerfControls.ThrottleN}");
        if (_intervalSegMsSum.Count > 0)
        {
            sb.Append(" | segments(avg ms):");
            foreach (var kv in _intervalSegMsSum.OrderByDescending(k => k.Value).Take(10))
                sb.Append($" {kv.Key}={kv.Value / n:0.000}");
        }
        sb.Append(" | windows(avg ms):");
        foreach (var kv in _intervalWindowMsSum.OrderByDescending(k => k.Value).Take(8))
            sb.Append($" {kv.Key}={kv.Value / n:0.000}");
        return sb.ToString();
    }

    private static void ResetInterval()
    {
        _intervalFrames = 0;
        _intervalFrameMsSum = 0.0;
        _intervalFrameMsMax = 0.0;
        _intervalDrawMsSum = 0.0;
        _intervalUpdateMsSum = 0.0;
        _intervalUpdateAllocSum = 0.0;
        _intervalHookCombatMsSum = 0.0;
        _intervalHookCombatAllocSum = 0.0;
        _intervalHookCombatCalls = 0L;
        _intervalHookWireMsSum = 0.0;
        _intervalHookWireAllocSum = 0.0;
        _intervalHookWireCalls = 0L;
        _intervalWindowMsSum.Clear();
        _intervalSegMsSum.Clear();
    }

    /// <summary>Immutable view of the last committed frame for the overlay.</summary>
    public static PerfSnapshot Snapshot()
        => new(_lastFrameMs, _lastDrawMs, _lastUpdateMs, new Dictionary<string, double>(_publishedWindowMs));

    // --- test seam (internal; exposed via InternalsVisibleTo) ---
    internal static void OverrideEnabledForTests(bool value) => _enabled = value;

    internal static void ResetForTests()
    {
        _drawSw.Reset();
        _drawMsThisFrame = 0.0;
        _updateSw.Reset();
        _updateMsThisFrame = 0.0;
        _windowMsThisFrame.Clear();
        _windowSw.Clear();
        _lastFrameMs = 0.0;
        _lastDrawMs = 0.0;
        _lastUpdateMs = 0.0;
        _updateAllocStart = 0;
        _updateAllocThisFrame = 0;
        _lastUpdateAllocBytes = 0;
        _hookCombatTicks = 0; _hookCombatAlloc = 0; _hookCombatCalls = 0;
        _lastHookCombatMs = 0; _lastHookCombatAlloc = 0; _lastHookCombatCalls = 0;
        _hookWireTicks = 0; _hookWireAlloc = 0; _hookWireCalls = 0;
        _lastHookWireMs = 0; _lastHookWireAlloc = 0; _lastHookWireCalls = 0;
        _publishedWindowMs.Clear();
        _drewThisFrame = false;
        _segSw.Clear();
        _segMsThisFrame.Clear();
        FrameCounter = 0;
        ResetInterval();
    }
}

/// <summary>Snapshot of one committed frame's timings.</summary>
public sealed class PerfSnapshot
{
    /// <summary>Constructs a frame snapshot from raw timing measurements.</summary>
    public PerfSnapshot(double lastFrameMs, double lastDrawMs, double lastUpdateMs, IReadOnlyDictionary<string, double> windowMs)
    {
        LastFrameMs = lastFrameMs;
        LastDrawMs = lastDrawMs;
        LastUpdateMs = lastUpdateMs;
        WindowMs = windowMs;
    }

    /// <summary>Total wall-clock duration of the last committed Unity frame in milliseconds.</summary>
    public double LastFrameMs { get; }
    /// <summary>Total OnGUI draw cost accumulated across all passes in the last frame, in milliseconds.</summary>
    public double LastDrawMs { get; }
    /// <summary>Total managed Update work cost in the last frame (plugins + services), in milliseconds.</summary>
    public double LastUpdateMs { get; }
    /// <summary>Per-window draw cost for the last frame, keyed by window id, in milliseconds.</summary>
    public IReadOnlyDictionary<string, double> WindowMs { get; }
    /// <summary>Estimated frames per second from the last frame duration; zero when no frame has been recorded.</summary>
    public double Fps => LastFrameMs > 0.0 ? 1000.0 / LastFrameMs : 0.0;
}
