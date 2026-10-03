using System;
using System.Runtime.InteropServices;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Binding to the add-on's C exports (ABI table: StellarReShadeBridge README). The add-on is a module ReShade
/// loads into the game process; it is looked up with <c>GetModuleHandleW</c> (never loaded by us) and every export
/// resolved with <c>GetProcAddress</c>. All exports are cdecl (one convention on x64).</summary>
internal sealed partial class ReShadeBridge
{
    internal const string ModuleName = "Stellar.ReShadeBridge.addon64";
    internal const int SupportedAbiVersion = 1;
    private const int BindRetryInterval = 120; // calls between GetModuleHandleW retries while the add-on is absent

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IntArgFn(int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TechniqueAtFn(int index, [In, Out] byte[] name, int nameLen, [In, Out] byte[] effect, int effectLen, out int enabled);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RequestTechniqueFn(byte[]? effect, byte[] name, int on, int save);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPresetFn([In, Out] byte[] buffer, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void StringArgFn(byte[] text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TwoStringArgFn(byte[]? first, byte[]? second);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void QueueRenderFn(IntPtr texture, uint width, uint height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PointerFn();

    private sealed class Exports
    {
        internal IntFn Ready = null!, IsLoading = null!, SnapshotFrames = null!, GetEnabled = null!, TechniqueCount = null!, LastRender = null!;
        internal IntArgFn RequestEnabled = null!;
        internal TechniqueAtFn TechniqueAt = null!;
        internal RequestTechniqueFn RequestTechnique = null!;
        internal GetPresetFn GetPreset = null!;
        internal StringArgFn RequestPreset = null!;
        internal TwoStringArgFn RequestSearchPaths = null!;
        internal QueueRenderFn QueueRender = null!;
        internal PointerFn RenderEventFunc = null!;
    }

    [DllImport("kernel32", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr GetModuleHandleW(string moduleName);

    [DllImport("kernel32", CharSet = CharSet.Ansi, ExactSpelling = true, BestFitMapping = false)]
    private static extern IntPtr GetProcAddress(IntPtr module, string procName);

    private Exports? _exports;
    private bool _refused;
    private int _retryCountdown;

    /// <summary>True once the add-on's exports are bound. Retries the module lookup every
    /// <see cref="BindRetryInterval"/> calls while absent; a wrong ABI version is refused for good (logged once).</summary>
    internal bool TryBind()
    {
        if (_exports is not null) return true;
        if (_refused || _retryCountdown-- > 0) return false;
        _retryCountdown = BindRetryInterval;
        try
        {
            var module = GetModuleHandleW(ModuleName);
            if (module == IntPtr.Zero) return false;
            var version = Bind<IntFn>(module, "rsb_version")();
            if (version != SupportedAbiVersion)
            {
                _refused = true;
                _log.Warning($"[ReShade] {ModuleName} reports ABI {version}, this framework speaks {SupportedAbiVersion} — not binding");
                return false;
            }
            _exports = BindAll(module);
            return true;
        }
        catch (Exception ex)
        {
            _refused = true; // no kernel32 (not Windows), an export missing, or a marshalling fault: never retry
            if (ex is not DllNotFoundException) _log.Warning($"[ReShade] could not bind {ModuleName}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static Exports BindAll(IntPtr module) => new()
    {
        Ready = Bind<IntFn>(module, "rsb_ready"),
        IsLoading = Bind<IntFn>(module, "rsb_is_loading"),
        SnapshotFrames = Bind<IntFn>(module, "rsb_snapshot_frames"),
        GetEnabled = Bind<IntFn>(module, "rsb_get_enabled"),
        RequestEnabled = Bind<IntArgFn>(module, "rsb_request_enabled"),
        TechniqueCount = Bind<IntFn>(module, "rsb_technique_count"),
        TechniqueAt = Bind<TechniqueAtFn>(module, "rsb_technique_at"),
        RequestTechnique = Bind<RequestTechniqueFn>(module, "rsb_request_technique"),
        GetPreset = Bind<GetPresetFn>(module, "rsb_get_preset"),
        RequestPreset = Bind<StringArgFn>(module, "rsb_request_preset"),
        RequestSearchPaths = Bind<TwoStringArgFn>(module, "rsb_request_search_paths"),
        QueueRender = Bind<QueueRenderFn>(module, "rsb_queue_render"),
        LastRender = Bind<IntFn>(module, "rsb_last_render"),
        RenderEventFunc = Bind<PointerFn>(module, "rsb_render_event_func"),
    };

    private static T Bind<T>(IntPtr module, string export) where T : Delegate
    {
        var address = GetProcAddress(module, export);
        if (address == IntPtr.Zero) throw new EntryPointNotFoundException(export);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }
}
