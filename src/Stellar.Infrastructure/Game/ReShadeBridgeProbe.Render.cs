using System;
using System.Diagnostics;
using System.IO;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY — part (b): one offscreen render try per ~0.5 s (never two in one frame — render_effects draws
/// once per present), until ReShade draws techniques AND the pixels change, or 20 s pass.</summary>
internal sealed partial class ReShadeBridgeProbe
{
    private sealed class RenderSeries
    {
        private const double IntervalS = 0.5, TimeoutS = 20;
        private readonly ReShadeBridgeProbe _p;
        private readonly string _label;
        private readonly int _scale;
        private readonly Stopwatch _clock = new();
        private double _lastTry = double.NegativeInfinity;
        private int _tries;

        public RenderSeries(ReShadeBridgeProbe p, string label, int scale) { _p = p; _label = label; _scale = scale; }

        public bool Step()
        {
            if (!_clock.IsRunning) _clock.Start();
            var now = _clock.Elapsed.TotalSeconds;
            if (now - _lastTry < IntervalS) return false;
            _lastTry = now;
            _tries++;
            var r = _p.RenderOnce(_scale);
            if (r is null) return _clock.Elapsed.TotalSeconds >= TimeoutS;
            var (before, after, w, h, last, info, diff) = r.Value;
            var ms = _clock.Elapsed.TotalMilliseconds;
            _p._log.Info(Tag + $"{_label} {_scale}x try={_tries} {w}x{h} last_render={last} meanAbsDiff={diff:F2} info=\"{info}\" t={ms:F0}ms");
            if (last > 0 && diff > 1)
            {
                _p._log.Info(Tag + $"{_label} {_scale}x FIRST SUCCESS tries={_tries} elapsedMs={ms:F0}");
                _p.SavePair(before, after, w, h, $"{_label}-{_scale}x");
                return true;
            }
            if (_clock.Elapsed.TotalSeconds < TimeoutS) return false;
            _p._log.Warning(Tag + $"{_label} {_scale}x TIMEOUT tries={_tries} elapsedMs={ms:F0} last_render={last} meanAbsDiff={diff:F2}");
            _p.SavePair(before, after, w, h, $"{_label}-{_scale}x-timeout");
            return true;
        }
    }

    /// <summary>Renders the main camera into a fresh RT, reads it, lets ReShade draw into it on the render thread, reads again.</summary>
    private (byte[] Before, byte[] After, int W, int H, int Last, string Info, double Diff)? RenderOnce(int scale)
    {
        var cam = Camera.main;
        if (cam == null) { _log.Warning(Tag + "no main camera"); return null; }
        int w = Screen.width * scale, h = Screen.height * scale;
        var rt = new RenderTexture(w, h, 24);   // the capture path's own RT shape (UnityFrameGrabber.Capture)
        var prev = cam.targetTexture;
        try
        {
            rt.Create();
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var before = Read(rt, w, h);
            B.Queue(rt.GetNativeTexturePtr(), (uint)w, (uint)h);
            GL.IssuePluginEvent(B.RenderEvent, 1);
            var after = Read(rt, w, h);   // ReadPixels syncs with the render thread, so render_event has run
            return (before, after, w, h, B.LastRender(), B.LastInfo(), MeanAbsDiff(before, after));
        }
        finally
        {
            cam.targetTexture = prev;
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }
    }

    private static byte[] Read(RenderTexture rt, int w, int h)
    {
        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            return CopyIl2Cpp(tex.GetRawTextureData());
        }
        finally
        {
            RenderTexture.active = prevActive;
            UnityEngine.Object.Destroy(tex);
        }
    }

    // One memcpy over the native buffer (UnityFrameGrabber.CopyIl2Cpp) — never element-by-element.
    private static byte[] CopyIl2Cpp(Il2CppStructArray<byte>? src) =>
        src is null ? Array.Empty<byte>() : src.AsSpan().ToArray();

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        long sum = 0;
        for (var i = 0; i < a.Length; i += 4)
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return sum / (a.Length / 4.0 * 3);
    }

    private void SavePair(byte[] before, byte[] after, int w, int h, string stem)
    {
        Save(before, w, h, stem + "-before.png");
        Save(after, w, h, stem + "-after.png");
    }

    private void Save(byte[] rgba, int w, int h, string file)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.LoadRawTextureData(rgba);
            File.WriteAllBytes(Path.Combine(_outDir, file), CopyIl2Cpp(ImageConversion.EncodeToPNG(tex)));
        }
        catch (Exception ex) { _log.Warning(Tag + "save " + file + ": " + ex.Message); }
        finally { UnityEngine.Object.Destroy(tex); }
    }
}
