using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY (branch probe/reshade-bridge, never merged) — spec 2026-10-03-photo-studio-reshade-design.md § 8.
/// Armed by RESHADEPROBE in stellar_perf.flags. 30 s after the world is active, once: find the bridge add-on, list techniques,
/// switch effects off/on, then render the main camera into a RenderTexture at 1× and 4×, let ReShade draw into it on the
/// render thread, and compare pixels before/after; repeat with DisplayDepth on. PNGs → stellar/screenshots/reshadeprobe/.</summary>
internal sealed class ReShadeBridgeProbe
{
    private const string Tag = "[RsProbe] ";
    private const string AddonName = "Stellar.ReShadeBridge.addon64";
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntIntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TechniqueCb(IntPtr name, int enabled, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn(TechniqueCb cb, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTechFn([MarshalAs(UnmanagedType.LPStr)] string name, int on);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int QueueFn(IntPtr tex, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();

    private readonly IPluginLog _log;
    private readonly string _outDir;
    private readonly List<(string Name, int On)> _techniques = new();
    private TechniqueCb? _keepAlive;
    private float _t;
    private bool _done;

    public ReShadeBridgeProbe(IPluginLog log, string gameMiniDir)
    {
        _log = log;
        _outDir = Path.Combine(gameMiniDir, "stellar", "screenshots", "reshadeprobe");
    }

    public static bool Armed => PerfControls.Flag("RESHADEPROBE");

    public void Tick(float dt)
    {
        if (_done) return;
        _t += dt;
        if (_t < 30f) return;
        _done = true;
        try { Run(); }
        catch (Exception ex) { _log.Warning(Tag + "threw: " + ex); }
    }

    private T Fn<T>(IntPtr mod, string name) where T : Delegate
    {
        var p = GetProcAddress(mod, name);
        if (p == IntPtr.Zero) throw new InvalidOperationException("missing export " + name);
        return Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    private void Run()
    {
        var mod = GetModuleHandleW(AddonName);
        _log.Info(Tag + $"addon module={(mod == IntPtr.Zero ? "NOT LOADED" : "0x" + mod.ToString("x"))}");
        if (mod == IntPtr.Zero) return;
        _log.Info(Tag + $"version={Fn<IntFn>(mod, "rsb_version")()} ready={Fn<IntFn>(mod, "rsb_ready")()} enabled={Fn<IntFn>(mod, "rsb_get_enabled")()}");
        _keepAlive = (n, on, _) => _techniques.Add((Marshal.PtrToStringAnsi(n) ?? "?", on));
        Fn<EnumFn>(mod, "rsb_enum_techniques")(_keepAlive, IntPtr.Zero);
        _log.Info(Tag + $"techniques={_techniques.Count}: " + string.Join(", ", _techniques.ConvertAll(t => t.Name + (t.On != 0 ? "*" : ""))));
        var setEnabled = Fn<IntIntFn>(mod, "rsb_set_enabled");
        _log.Info(Tag + $"set_enabled(0)={setEnabled(0)} -> {Fn<IntFn>(mod, "rsb_get_enabled")()}; set_enabled(1)={setEnabled(1)} -> {Fn<IntFn>(mod, "rsb_get_enabled")()}");
        var queue = Fn<QueueFn>(mod, "rsb_queue_render");
        var last = Fn<IntFn>(mod, "rsb_last_render");
        var evt = Fn<PtrFn>(mod, "rsb_render_event_func")();
        Directory.CreateDirectory(_outDir);
        RenderTest("fx", 1, queue, last, evt);
        RenderTest("fx", 4, queue, last, evt);
        _log.Info(Tag + $"set_technique(DisplayDepth,1)={Fn<SetTechFn>(mod, "rsb_set_technique")("DisplayDepth", 1)}");
        RenderTest("depth", 1, queue, last, evt);
        RenderTest("depth", 4, queue, last, evt);
        Fn<SetTechFn>(mod, "rsb_set_technique")("DisplayDepth", 0);
    }

    private void RenderTest(string label, int scale, QueueFn queue, IntFn last, IntPtr evt)
    {
        var cam = Camera.main;
        if (cam == null) { _log.Warning(Tag + "no main camera"); return; }
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
            queue(rt.GetNativeTexturePtr(), (uint)w, (uint)h);
            GL.IssuePluginEvent(evt, 1);
            var after = Read(rt, w, h);
            var diff = MeanAbsDiff(before, after);
            _log.Info(Tag + $"{label} {scale}x {w}x{h} last_render={last()} meanAbsDiff={diff:F2}");
            Save(before, w, h, $"{label}-{scale}x-before.png");
            Save(after, w, h, $"{label}-{scale}x-after.png");
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
            Il2CppStructArray<byte> raw = tex.GetRawTextureData();
            var bytes = new byte[raw.Length];
            for (var i = 0; i < raw.Length; i++) bytes[i] = raw[i];
            return bytes;
        }
        finally
        {
            RenderTexture.active = prevActive;
            UnityEngine.Object.Destroy(tex);
        }
    }

    private static double MeanAbsDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length || a.Length == 0) return -1;
        long sum = 0;
        for (var i = 0; i < a.Length; i += 4)
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
        return sum / (a.Length / 4.0 * 3);
    }

    private void Save(byte[] rgba, int w, int h, string file)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        try
        {
            tex.LoadRawTextureData(rgba);
            var png = ImageConversion.EncodeToPNG(tex);
            File.WriteAllBytes(Path.Combine(_outDir, file), png);
        }
        catch (Exception ex) { _log.Warning(Tag + "save " + file + ": " + ex.Message); }
        finally { UnityEngine.Object.Destroy(tex); }
    }
}
