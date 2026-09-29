using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Stellar.Abstractions.Diagnostics;

/// <summary>
/// Render-frame hitch attribution. When a rendered frame exceeds <see cref="ThresholdMs"/>, one
/// <c>[Hitch]</c> line names what Stellar did inside that frame — the framework tick, each plugin's
/// Update, font-atlas rebuild refreshes, window mounts/destroys, config saves, combat-hook time — plus the
/// managed and IL2CPP GC collections that ran. A hitch with near-zero Stellar time and no GC is the game's
/// own (render / shader compile / asset load), which is exactly the question a frametime-spike report needs
/// answered first. Gated on the one diagnostics flag (<see cref="StellarDiagnostics"/>) or a <c>HITCH</c>
/// line in <c>stellar_perf.flags</c>; when off every call returns on a cached field read.
/// <para>Main-thread buckets use a plain dictionary (all callers are on the Unity main thread); the
/// combat-hook total is fed from hook threads and accumulates with <see cref="Interlocked"/>.</para>
/// <para>Static mutable state is the sanctioned diagnostic exception, as for <see cref="PerfProbe"/>.</para>
/// </summary>
public static class HitchProbe
{
    private static readonly bool _enabled = StellarDiagnostics.IsEnabled || PerfControls.Flag("HITCH");

    /// <summary>A rendered frame slower than this (ms) is reported.</summary>
    public const double ThresholdMs = 60.0;

    // Buckets that cost less than this (ms) within the frame are summarised as "rest" to keep the line short.
    private const double MinReportMs = 0.5;

    private static readonly Dictionary<string, Bucket> _buckets = new();
    // [0] = combat hooks, [1] = wire/notify dispatch hooks (may run off the main thread — reported, not summed).
    private static readonly long[] _hookTicks = new long[2];
    private static readonly long[] _hookCalls = new long[2];
    private static int _lastGc0 = -1, _lastGc1, _lastGc2, _lastIl2CppGc = -1;
    private static readonly StringBuilder _sb = new(512);

    private sealed class Bucket { public long Ticks; public int Calls; }

    /// <summary><c>true</c> when hitch attribution is armed (diagnostics on, or <c>HITCH</c> flag).</summary>
    public static bool IsEnabled => _enabled;

    /// <summary>Start timestamp for a timed section, or 0 when disabled.</summary>
    public static long Begin() => _enabled ? Stopwatch.GetTimestamp() : 0L;

    /// <summary>Close a section opened with <see cref="Begin"/> and charge it to <paramref name="bucket"/>.
    /// Main thread only. Use stable (non-allocated) bucket strings.</summary>
    public static void End(string bucket, long startTicks)
    {
        if (!_enabled || startTicks == 0L) return;
        var elapsed = Stopwatch.GetTimestamp() - startTicks;
        if (!_buckets.TryGetValue(bucket, out var b)) _buckets[bucket] = b = new Bucket();
        b.Ticks += elapsed;
        b.Calls++;
    }

    /// <summary><see cref="End"/> then return a fresh start stamp — for back-to-back phases.</summary>
    public static long Lap(string bucket, long startTicks)
    {
        if (!_enabled) return 0L;
        End(bucket, startTicks);
        return Stopwatch.GetTimestamp();
    }

    private static readonly Dictionary<string, long> _segStart = new();

    /// <summary>Open a named segment (main thread); pairs with <see cref="EndSeg"/>. Fed by
    /// <see cref="PerfProbe.BeginSeg"/> so the existing tick segments break a hitch down with no extra call sites.</summary>
    public static void BeginSeg(string name)
    {
        if (_enabled) _segStart[name] = Stopwatch.GetTimestamp();
    }

    /// <summary>Close a segment opened with <see cref="BeginSeg"/>.</summary>
    public static void EndSeg(string name)
    {
        if (_enabled && _segStart.TryGetValue(name, out var t)) End(name, t);
    }

    /// <summary>Charge one hook invocation (any thread). <paramref name="kind"/>: 0 = combat, 1 = wire/notify.</summary>
    public static void AddHook(int kind, long startTicks)
    {
        if (!_enabled || startTicks == 0L || (uint)kind > 1u) return;
        Interlocked.Add(ref _hookTicks[kind], Stopwatch.GetTimestamp() - startTicks);
        Interlocked.Increment(ref _hookCalls[kind]);
    }

    /// <summary>Called once per rendered frame (main thread) with that frame's duration. Emits a line through
    /// <paramref name="log"/> when the frame was a hitch, then resets the per-frame accumulators.</summary>
    /// <param name="frameMs">Unscaled duration of the frame that just ended, in ms.</param>
    /// <param name="il2CppGcCount">The game's IL2CPP GC collection count (monotonic), or -1 if unknown.</param>
    /// <param name="log">Log sink for the hitch line.</param>
    public static void OnRenderFrame(double frameMs, int il2CppGcCount, System.Action<string> log)
    {
        if (!_enabled) return;
        int g0 = System.GC.CollectionCount(0), g1 = System.GC.CollectionCount(1), g2 = System.GC.CollectionCount(2);
        if (frameMs >= ThresholdMs && _lastGc0 >= 0) log(Format(frameMs, g0, g1, g2, il2CppGcCount));
        _lastGc0 = g0; _lastGc1 = g1; _lastGc2 = g2; _lastIl2CppGc = il2CppGcCount;
        foreach (var b in _buckets.Values) { b.Ticks = 0; b.Calls = 0; }
        for (var k = 0; k < 2; k++) { Interlocked.Exchange(ref _hookTicks[k], 0L); Interlocked.Exchange(ref _hookCalls[k], 0L); }
    }

    private static void AppendHook(StringBuilder sb, string label, int kind)
    {
        var calls = Interlocked.Read(ref _hookCalls[kind]);
        if (calls == 0) return;
        var ms = Interlocked.Read(ref _hookTicks[kind]) * 1000.0 / Stopwatch.Frequency;
        sb.Append(label).Append(ms.ToString("0.0")).Append("ms/").Append(calls);
    }

    private static string Format(double frameMs, int g0, int g1, int g2, int il2Cpp)
    {
        var sb = _sb.Clear();
        sb.Append("[Hitch] frame=").Append(frameMs.ToString("0.0")).Append("ms");
        double restMs = 0; var restCalls = 0;
        foreach (var kv in _buckets)
        {
            if (kv.Value.Calls == 0) continue;
            var ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency;
            if (ms < MinReportMs) { restMs += ms; restCalls += kv.Value.Calls; continue; }
            sb.Append(' ').Append(kv.Key).Append('=').Append(ms.ToString("0.0")).Append("ms/").Append(kv.Value.Calls);
        }
        if (restCalls > 0) sb.Append(" rest=").Append(restMs.ToString("0.0")).Append("ms/").Append(restCalls);
        AppendHook(sb, " combatHooks=", 0);
        AppendHook(sb, " wireHooks=", 1);
        sb.Append(" gc=+").Append(g0 - _lastGc0).Append("/+").Append(g1 - _lastGc1).Append("/+").Append(g2 - _lastGc2);
        sb.Append(" il2cppGc=");
        if (il2Cpp >= 0 && _lastIl2CppGc >= 0) sb.Append('+').Append(il2Cpp - _lastIl2CppGc); else sb.Append('?');
        return sb.ToString();
    }
}
