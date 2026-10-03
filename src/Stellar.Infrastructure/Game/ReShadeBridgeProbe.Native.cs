using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
namespace Stellar.Infrastructure.Game;

/// <summary>PROBE ONLY — the bridge add-on's C ABI (tools/probes/reshade-bridge/bridge.cpp, rsb_version 2).</summary>
internal sealed partial class ReShadeBridgeProbe
{
    private const string AddonName = "Stellar.ReShadeBridge.addon64";
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntIntFn(int a);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TechniqueCb(IntPtr name, int enabled, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumFn(TechniqueCb cb, IntPtr user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTechFn([MarshalAs(UnmanagedType.LPStr)] string name, int on);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int QueueFn(IntPtr tex, uint w, uint h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InfoFn(IntPtr buf, int len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PtrFn();

    /// <summary>Bound exports. Setters only ENQUEUE (applied in reshade_present); getters read the per-frame snapshot.</summary>
    private sealed class Bridge
    {
        public readonly IntPtr Module;
        public readonly IntFn Version, Ready, GetEnabled, IsLoading, SnapshotFrames, LastRender;
        public readonly IntIntFn SetEnabled;
        public readonly EnumFn Enum;
        public readonly SetTechFn SetTechnique;
        public readonly QueueFn Queue;
        public readonly InfoFn Info;
        public readonly IntPtr RenderEvent;
        private readonly List<(string Name, int On)> _scratch = new();
        private readonly TechniqueCb _cb;   // kept alive for the native callback

        public Bridge(IntPtr mod)
        {
            Module = mod;
            Version = Fn<IntFn>(mod, "rsb_version"); Ready = Fn<IntFn>(mod, "rsb_ready");
            GetEnabled = Fn<IntFn>(mod, "rsb_get_enabled"); IsLoading = Fn<IntFn>(mod, "rsb_is_loading");
            SnapshotFrames = Fn<IntFn>(mod, "rsb_snapshot_frames"); LastRender = Fn<IntFn>(mod, "rsb_last_render");
            SetEnabled = Fn<IntIntFn>(mod, "rsb_set_enabled"); Enum = Fn<EnumFn>(mod, "rsb_enum_techniques");
            SetTechnique = Fn<SetTechFn>(mod, "rsb_set_technique"); Queue = Fn<QueueFn>(mod, "rsb_queue_render");
            Info = Fn<InfoFn>(mod, "rsb_last_info");
            RenderEvent = Fn<PtrFn>(mod, "rsb_render_event_func")();
            _cb = (n, on, _) => _scratch.Add((Marshal.PtrToStringAnsi(n) ?? "?", on));
        }

        /// <summary>The last snapshot the render thread published (empty while ReShade is loading).</summary>
        public List<(string Name, int On)> Techniques()
        {
            _scratch.Clear();
            Enum(_cb, IntPtr.Zero);
            return new List<(string Name, int On)>(_scratch);
        }

        /// <summary>-1 = not in the snapshot, else 0/1.</summary>
        public int TechniqueState(string name)
        {
            foreach (var t in Techniques()) if (t.Name == name) return t.On;
            return -1;
        }

        public string LastInfo()
        {
            var buf = Marshal.AllocHGlobal(256);
            try { Info(buf, 256); return Marshal.PtrToStringAnsi(buf) ?? ""; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static T Fn<T>(IntPtr mod, string name) where T : Delegate
        {
            var p = GetProcAddress(mod, name);
            if (p == IntPtr.Zero) throw new InvalidOperationException("missing export " + name);
            return Marshal.GetDelegateForFunctionPointer<T>(p);
        }
    }
}
