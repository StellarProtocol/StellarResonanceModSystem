using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Stellar.Abstractions.Diagnostics;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// HITCH-only: WHAT Unity's canvas update spends <c>ui:canvasRebuild</c> on. Sampled at
/// <c>Canvas.preWillRenderCanvases</c> (CanvasRebuildProbe.OnPre), i.e. after every Update/LateUpdate dirtied this
/// frame's elements and just before uGUI and TMP process them:
/// <list type="bullet">
/// <item>EVERY frame, the queue LENGTHS: uGUI CanvasUpdateRegistry layout/graphic, TMP_UpdateManager layout/graphic/
/// culling (+ the size of TMP's per-frame InternalUpdate list). Averaged per frame on the summary.</item>
/// <item>Every <see cref="SampleEvery"/>th frame, the queue CONTENTS as transform paths, counted across the window, so
/// an element re-dirtied every frame floats to the top.</item>
/// <item>Once per window: the registered RectMask2D count and a timed EXTRA <c>ClipperRegistry.Cull()</c> (per-frame
/// clipping work that never shows up in a rebuild queue — it runs inside the same PerformUpdate), and a census of
/// this canvas's CanvasRenderers whose <c>hasMoved</c> is set (moved this frame → re-culled / re-batched).</item>
/// </list>
/// Logged every ~10 s as <c>[UiPerf.Queues]</c> lines. Each part fails closed on its own (one log line) so a
/// missing interop member never breaks the frame.
/// </summary>
internal static class CanvasQueueProbe
{
    private const int SampleEvery = 30;      // contents sampled on every 30th frame (~4×/s at 120 fps)
    private const int MaxPerQueue = 400;     // per-sample walk cap per queue
    private const int TopN = 10;
    private const double WindowSec = 10.0;
    private static readonly string[] QueueNames = { "uiLayout", "uiGraphic", "tmpLayout", "tmpGraphic", "tmpCulling" };

    internal static Transform? Root;         // the shared Stellar window canvas (hasMoved census scope)
    private static Action<string>? _log;
    private static bool _uiBroken, _tmpBroken, _clipBroken, _movedBroken;
    private static long _windowStart;
    private static int _frames, _samples, _tmpInternalMax;
    private static readonly long[] _lenSum = new long[QueueNames.Length];
    private static readonly int[] _lenMax = new int[QueueNames.Length];
    private static readonly Dictionary<string, int>[] _hits = NewHits();
    private static readonly Dictionary<IntPtr, string> _pathCache = new();
    private static bool _censusDone;
    private static string _census = "";
    private static readonly StringBuilder _sb = new(1024);

    private static Dictionary<string, int>[] NewHits()
    {
        var a = new Dictionary<string, int>[QueueNames.Length + 1];   // +1 = "moved" (hasMoved census)
        for (var i = 0; i < a.Length; i++) a[i] = new Dictionary<string, int>();
        return a;
    }

    /// <summary>Called from CanvasRebuildProbe at preWillRenderCanvases (main thread, HITCH only).</summary>
    public static void OnPreRender(Action<string> log)
    {
        if (!HitchProbe.IsEnabled) return;
        _log = log;
        var now = Stopwatch.GetTimestamp();
        if (_windowStart == 0L) _windowStart = now;
        var sample = ++_frames % SampleEvery == 0;
        if (sample) _samples++;
        ReadUi(sample);
        ReadTmp(sample);
        if (sample && !_censusDone) { _censusDone = true; _census = Census(); }
        if ((now - _windowStart) / (double)Stopwatch.Frequency >= WindowSec) Flush(now);
    }

    private static void ReadUi(bool sample)
    {
        if (_uiBroken) return;
        try
        {
            var reg = CanvasUpdateRegistry.instance;
            var layout = reg.m_LayoutRebuildQueue; var graphic = reg.m_GraphicRebuildQueue;
            AddLen(0, layout.Count); AddLen(1, graphic.Count);
            if (!sample) return;
            for (var i = 0; i < layout.Count && i < MaxPerQueue; i++) Hit(0, layout[i]?.transform);
            for (var i = 0; i < graphic.Count && i < MaxPerQueue; i++) Hit(1, graphic[i]?.transform);
        }
        catch (Exception ex) { _uiBroken = true; _log?.Invoke($"[UiPerf.Queues] uGUI queues unreadable: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void ReadTmp(bool sample)
    {
        if (_tmpBroken) return;
        try
        {
            var m = TMP_UpdateManager.instance;
            var layout = m.m_LayoutRebuildQueue; var graphic = m.m_GraphicRebuildQueue; var cull = m.m_CullingUpdateQueue;
            AddLen(2, layout.Count); AddLen(3, graphic.Count); AddLen(4, cull.Count);
            var internalN = m.m_InternalUpdateQueue.Count;
            if (internalN > _tmpInternalMax) _tmpInternalMax = internalN;
            if (!sample) return;
            for (var i = 0; i < layout.Count && i < MaxPerQueue; i++) Hit(2, layout[i]?.transform);
            for (var i = 0; i < graphic.Count && i < MaxPerQueue; i++) Hit(3, graphic[i]?.transform);
            for (var i = 0; i < cull.Count && i < MaxPerQueue; i++) Hit(4, cull[i]?.transform);
        }
        catch (Exception ex) { _tmpBroken = true; _log?.Invoke($"[UiPerf.Queues] TMP queues unreadable: {ex.GetType().Name}: {ex.Message}"); }
    }

    private static void AddLen(int q, int n) { _lenSum[q] += n; if (n > _lenMax[q]) _lenMax[q] = n; }

    private static void Hit(int q, Transform? t)
    {
        if (t == null) return;
        var path = PathOf(t);
        var d = _hits[q];
        d[path] = d.TryGetValue(path, out var n) ? n + 1 : 1;
    }

    // Up to 6 levels, leaf last ("…/Grand/Parent/Leaf"). Cached by native pointer for the window (names are interop
    // string marshals — only paid once per element per window, and only on sample frames).
    private static string PathOf(Transform t)
    {
        var key = t.Pointer;
        if (_pathCache.TryGetValue(key, out var p)) return p;
        var parts = new List<string>(6);
        var cur = t;
        for (var depth = 0; cur != null && depth < 6; depth++) { parts.Add(cur.name); cur = cur.parent; }
        parts.Reverse();
        p = (cur != null ? "…/" : "") + string.Join("/", parts);
        _pathCache[key] = p;
        return p;
    }

    // Once per window: clipper count + one timed extra ClipperRegistry.Cull (idempotent — the real pass this frame then
    // finds the clip rects unchanged), and the hasMoved census of the Stellar canvas.
    private static string Census()
    {
        var sb = new StringBuilder(128);
        if (!_clipBroken)
        {
            try
            {
                var cr = ClipperRegistry.instance;
                var n = cr.m_Clippers.Count;
                var t0 = Stopwatch.GetTimestamp();
                cr.Cull();
                var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                sb.Append(" clippers=").Append(n).Append(" clipCull=").Append(ms.ToString("0.00")).Append("ms");
            }
            catch (Exception ex) { _clipBroken = true; sb.Append(" clip=n/a(").Append(ex.GetType().Name).Append(')'); }
        }
        sb.Append(MovedCensus());
        return sb.ToString();
    }

    private static string MovedCensus()
    {
        if (_movedBroken || Root == null) return "";
        try
        {
            var rs = Root.GetComponentsInChildren<CanvasRenderer>(false);
            var moved = 0;
            for (var i = 0; i < rs.Length; i++)
            {
                var r = rs[i];
                if (r == null || !r.hasMoved) continue;
                moved++;
                if (moved <= MaxPerQueue) Hit(QueueNames.Length, r.transform);
            }
            return $" stellarRenderers={rs.Length} hasMoved={moved}";
        }
        catch (Exception ex) { _movedBroken = true; return $" moved=n/a({ex.GetType().Name})"; }
    }

    private static void Flush(long now)
    {
        var log = _log;
        if (log != null && _frames > 0)
        {
            var sec = (now - _windowStart) / (double)Stopwatch.Frequency;
            var sb = _sb.Clear();
            sb.Append("[UiPerf.Queues] ").Append(sec.ToString("0.0")).Append("s frames=").Append(_frames).Append(" avg/frame(max):");
            for (var q = 0; q < QueueNames.Length; q++)
                sb.Append(' ').Append(QueueNames[q]).Append('=').Append(((double)_lenSum[q] / _frames).ToString("0.0"))
                  .Append('(').Append(_lenMax[q]).Append(')');
            sb.Append(" tmpInternalUpdate=").Append(_tmpInternalMax).Append(" |").Append(_census);
            log(sb.ToString());
            for (var q = 0; q <= QueueNames.Length; q++) LogTop(log, q);
        }
        Reset(now);
    }

    // One line per non-empty queue: "top uiGraphic (N sampled frames): 12×path; 9×path; …". A count equal to the
    // sampled-frame count = re-dirtied EVERY sampled frame (the culprit pattern). "moved" is from the single census.
    private static void LogTop(Action<string> log, int q)
    {
        var d = _hits[q];
        if (d.Count == 0) return;
        var list = new List<KeyValuePair<string, int>>(d);
        list.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = _sb.Clear();
        var name = q < QueueNames.Length ? QueueNames[q] : "moved";
        sb.Append("[UiPerf.Queues] top ").Append(name).Append(" (").Append(q < QueueNames.Length ? _samples : 1)
          .Append(" sampled frames, ").Append(d.Count).Append(" distinct):");
        for (var i = 0; i < list.Count && i < TopN; i++) sb.Append(' ').Append(list[i].Value).Append('×').Append(list[i].Key).Append(';');
        log(sb.ToString());
    }

    private static void Reset(long now)
    {
        _windowStart = now; _frames = 0; _samples = 0; _tmpInternalMax = 0;
        Array.Clear(_lenSum, 0, _lenSum.Length); Array.Clear(_lenMax, 0, _lenMax.Length);
        for (var i = 0; i < _hits.Length; i++) _hits[i].Clear();
        _pathCache.Clear();
        _censusDone = false; _census = "";
    }
}
