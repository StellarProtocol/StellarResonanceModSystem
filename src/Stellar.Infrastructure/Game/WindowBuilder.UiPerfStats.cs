using System;
using System.Diagnostics;
using System.Text;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>Window-toolkit work counters fed into the <c>[Hitch]</c> line (as <c>name=#N</c>).</summary>
internal enum UiCounter
{
    /// <summary>A BubbleElement width re-measure (one forced layout rebuild of its content child).</summary>
    BubbleMeasure,
    /// <summary>A variable-list row height measure while not yet latched (one forced rebuild of the slot).</summary>
    RowMeasure,
    /// <summary>A row that never settled within the try cap and was latched at its larger reading.</summary>
    RowForceLatch,
    /// <summary>A window apply with a structural change (the whole window is marked for a layout rebuild).</summary>
    Structural,
}

/// <summary>
/// Window-toolkit perf attribution, armed exactly when <see cref="HitchProbe"/> is (diagnostics flag or <c>HITCH</c>);
/// off = one cached field read per call. Two outputs:
/// <list type="bullet">
/// <item>per-frame counters and the <c>ui:ticker</c> / <c>ui:canvasRebuild</c> time buckets on each <c>[Hitch]</c> line;</item>
/// <item>a <c>[UiPerf]</c> summary every ~10 s: average/max frame, average/max window apply, ticker and canvas-rebuild
/// time, and the counter totals. A 60 ms hitch threshold never logs the typical 20-50 ms apply frame, so a steady
/// sub-threshold FPS loss (the ChatTools-at-rest 113→40 fps report) is only visible here.</item>
/// </list>
/// Main thread only. Static mutable state is the sanctioned diagnostic exception (as for HitchProbe). Kept in a
/// WindowBuilder.* file with only Abstractions dependencies so a sandbox that links the builder still compiles.
/// </summary>
internal static class UiPerfStats
{
    private static readonly string[] Names = { "ui:bubbleMeasure", "ui:rowMeasure", "ui:rowForceLatch", "ui:structural" };
    private const double SummaryEverySec = 10.0;
    private static readonly long[] _totals = new long[Names.Length];
    private static long _windowStart;
    private static int _frames; private static double _frameSum, _frameMax;
    private static int _applies; private static long _applyTicks, _applyMax; private static string? _applyMaxId;
    private static long _tickerTicks, _tickerMax;
    private static int _canvasN; private static long _canvasTicks, _canvasMax;
    private static readonly StringBuilder _sb = new(256);

    public static bool On => HitchProbe.IsEnabled;

    /// <summary>Count one (or <paramref name="n"/>) toolkit event, on the hitch line and in the summary.</summary>
    public static void Count(UiCounter c, int n = 1)
    {
        if (!HitchProbe.IsEnabled) return;
        HitchProbe.Count(Names[(int)c], n);
        _totals[(int)c] += n;
    }

    /// <summary>A window apply that started at <paramref name="startTicks"/> (0 = disarmed) just finished.
    /// The [Hitch] line already carries it (WindowService's per-window bucket); this feeds the summary only.</summary>
    public static void AddApply(string windowId, long startTicks)
    {
        if (startTicks == 0L) return;
        var d = Stopwatch.GetTimestamp() - startTicks;
        _applies++; _applyTicks += d;
        if (d > _applyMax) { _applyMax = d; _applyMaxId = windowId; }
    }

    /// <summary>WindowInteractionTicker.Update finished (bucket <c>ui:ticker</c>).</summary>
    public static void AddTicker(long startTicks)
    {
        if (startTicks == 0L) return;
        var d = Stopwatch.GetTimestamp() - startTicks;
        HitchProbe.End("ui:ticker", startTicks);
        _tickerTicks += d; if (d > _tickerMax) _tickerMax = d;
    }

    /// <summary>Unity's canvas update (layout + graphic rebuild), opened at <paramref name="startTicks"/>, just
    /// finished (bucket <c>ui:canvasRebuild</c>).</summary>
    public static void AddCanvas(long startTicks)
    {
        if (startTicks == 0L) return;
        var d = Stopwatch.GetTimestamp() - startTicks;
        HitchProbe.End("ui:canvasRebuild", startTicks);
        _canvasN++; _canvasTicks += d; if (d > _canvasMax) _canvasMax = d;
    }

    /// <summary>Once per rendered frame (the ticker's hitch hook): accumulate the frame and log the summary when due.</summary>
    public static void OnFrame(double frameMs, Action<string> log)
    {
        if (!HitchProbe.IsEnabled) return;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0L) { _windowStart = now; return; }   // the first frame only opens the window
        _frames++; _frameSum += frameMs; if (frameMs > _frameMax) _frameMax = frameMs;
        var sec = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (sec < SummaryEverySec) return;
        log(Format(sec));
        Reset(now);
    }

    private static string Format(double sec)
    {
        var sb = _sb.Clear();
        sb.Append("[UiPerf] ").Append(sec.ToString("0.0")).Append("s frames=").Append(_frames)
          .Append(" avg=").Append(Avg(_frameSum, _frames).ToString("0.0")).Append("ms max=").Append(_frameMax.ToString("0.0")).Append("ms");
        sb.Append(" | applies=").Append(_applies).Append(" avg=").Append(Ms(_applyTicks, _applies).ToString("0.00"))
          .Append("ms max=").Append(Ms(_applyMax, 1).ToString("0.0")).Append("ms(").Append(_applyMaxId ?? "-").Append(')');
        sb.Append(" | ticker avg=").Append(Ms(_tickerTicks, _frames).ToString("0.00")).Append("ms max=").Append(Ms(_tickerMax, 1).ToString("0.0")).Append("ms");
        sb.Append(" | canvasRebuild");
        if (_canvasN == 0) sb.Append("=n/a");
        else sb.Append(" avg=").Append(Ms(_canvasTicks, _canvasN).ToString("0.00")).Append("ms max=").Append(Ms(_canvasMax, 1).ToString("0.0")).Append("ms");
        sb.Append(" | per apply:");
        for (var i = 0; i < Names.Length; i++)
            sb.Append(' ').Append(Names[i], 3, Names[i].Length - 3).Append('=').Append(Avg(_totals[i], _applies).ToString("0.0"))
              .Append(" (").Append(_totals[i]).Append(')');
        return sb.ToString();
    }

    private static double Avg(double sum, int n) => n > 0 ? sum / n : 0.0;
    private static double Ms(long ticks, int n) => n > 0 ? ticks * 1000.0 / Stopwatch.Frequency / n : 0.0;

    private static void Reset(long now)
    {
        _windowStart = now;
        _frames = 0; _frameSum = 0; _frameMax = 0;
        _applies = 0; _applyTicks = 0; _applyMax = 0; _applyMaxId = null;
        _tickerTicks = 0; _tickerMax = 0;
        _canvasN = 0; _canvasTicks = 0; _canvasMax = 0;
        Array.Clear(_totals, 0, _totals.Length);
    }
}
