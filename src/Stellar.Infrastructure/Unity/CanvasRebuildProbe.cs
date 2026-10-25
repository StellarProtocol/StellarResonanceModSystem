using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Infrastructure.Game;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Times Unity's per-frame canvas update — <c>Canvas.preWillRenderCanvases</c> → the end of
/// <c>Canvas.willRenderCanvases</c> — as the <c>ui:canvasRebuild</c> hitch bucket and the <c>[UiPerf]</c> summary.
/// uGUI's CanvasUpdateRegistry (all layout + graphic rebuilds, TMP mesh generation included) subscribes to
/// willRenderCanvases at startup, long before us, so our later-subscribed handler runs after it and the span covers that
/// work. The native batch build that follows is NOT included. Installed only while HITCH/diagnostics is on (never in a
/// normal session); a failure to subscribe is logged once and the summary then reports <c>canvasRebuild=n/a</c>.
/// Main thread only (Unity raises both events from the player loop).
/// </summary>
internal static class CanvasRebuildProbe
{
    private static bool _installed;
    private static long _start;
    private static Action<string>? _log;
    // Cached so the IL2CPP delegate wrappers stay alive for the session (never unsubscribed; the probe is session-long).
    private static Canvas.WillRenderCanvases? _pre, _post;

    public static void EnsureInstalled(Action<string> log)
    {
        if (_installed || !HitchProbe.IsEnabled) return;
        _log = log;
        _installed = true;   // one attempt per session, success or not
        try
        {
            _pre = (Action)OnPre;
            _post = (Action)OnPost;
            Canvas.preWillRenderCanvases += _pre;
            Canvas.willRenderCanvases += _post;
            log("[UiPerf] canvas-rebuild timing hooked (Canvas.preWillRenderCanvases → willRenderCanvases)");
        }
        catch (Exception ex) { log($"[UiPerf] canvas-rebuild timing unavailable: {ex.GetType().Name}: {ex.Message}"); }
    }

    // The queue probe runs FIRST, so its own cost (and its once-per-window timed extra Cull) stays out of ui:canvasRebuild.
    private static void OnPre()
    {
        if (_log != null) CanvasQueueProbe.OnPreRender(_log);
        _start = HitchProbe.Begin();
    }

    private static void OnPost()
    {
        if (_start == 0L) return;   // a willRender without a pre (first frame after install) is not timed
        UiPerfStats.AddCanvas(_start);
        _start = 0L;
    }
}
