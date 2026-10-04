using System;
using System.Runtime.InteropServices;

namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// The add-on's OPTIONAL isolated-capture exports (bridge 1.1.0+, README § "Isolated capture"): a separate effect
/// runtime sized to the photo. <c>rsb_version()</c> stays 1, so they are detected one by one with
/// <c>GetProcAddress</c>; a 1.0.0 add-on simply lacks them (<see cref="IsolatedSupported"/> false) and photos use the
/// size guard. Never throws: every call returns a failure code when unbound or faulting. Main thread only, except
/// <see cref="IsolatedEventFunc"/>'s target, which Unity runs on the render thread.
/// </summary>
internal sealed partial class ReShadeBridge
{
    /// <summary>The code every isolated call returns when the exports are missing or a call faulted (the bridge's
    /// "internal error").</summary>
    internal const int IsolatedInternalError = -9;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IsoBeginFn(uint width, uint height, byte[] configPath);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IsoTechniqueFn(byte[]? effect, byte[] name, int on);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void IsoQueueFn(IntPtr texture);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void VoidFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TextOutFn([In, Out] byte[] buffer, int length);

    private sealed class IsolatedExports
    {
        internal IsoBeginFn Begin = null!;
        internal IntFn State = null!, LastRender = null!;
        internal IsoTechniqueFn RequestTechnique = null!;
        internal IsoQueueFn QueueRender = null!;
        internal VoidFn End = null!;
        internal PointerFn EventFunc = null!;
        internal TextOutFn AddonVersion = null!;
    }

    private IsolatedExports? _isolated;

    /// <summary>True when the bound add-on exports the isolated capture (1.1.0+).</summary>
    internal bool IsolatedSupported => TryBind() && _isolated is not null;

    private static IsolatedExports? TryBindIsolated(IntPtr module)
    {
        try
        {
            return new IsolatedExports
            {
                Begin = Bind<IsoBeginFn>(module, "rsb_isolated_begin"),
                State = Bind<IntFn>(module, "rsb_isolated_state"),
                LastRender = Bind<IntFn>(module, "rsb_isolated_last_render"),
                RequestTechnique = Bind<IsoTechniqueFn>(module, "rsb_isolated_request_technique"),
                QueueRender = Bind<IsoQueueFn>(module, "rsb_isolated_queue_render"),
                End = Bind<VoidFn>(module, "rsb_isolated_end"),
                EventFunc = Bind<PointerFn>(module, "rsb_isolated_event_func"),
                AddonVersion = Bind<TextOutFn>(module, "rsb_addon_version"),
            };
        }
        catch (Exception)
        {
            return null;   // a 1.0.0 add-on: no isolated capture, photos use the size guard
        }
    }

    /// <summary>The add-on's version text (e.g. "1.1.0"), or null before 1.1.0 / when unbound.</summary>
    internal string? AddonVersion()
    {
        if (!IsolatedSupported) return null;
        try
        {
            var buffer = new byte[64];
            return _isolated!.AddonVersion(buffer, buffer.Length) > 0 ? DecodeZ(buffer) : null;
        }
        catch (Exception ex)
        {
            Fault(ex);
            return null;
        }
    }

    /// <summary><c>rsb_isolated_begin</c>: 1 queued, -2 bad argument, -10 busy; <see cref="IsolatedInternalError"/>
    /// when unsupported.</summary>
    internal int IsolatedBegin(int width, int height, string configPath)
    {
        if (!IsolatedSupported || width <= 0 || height <= 0) return IsolatedInternalError;
        var path = EncodeZ(configPath);
        if (path is null) return IsolatedInternalError;
        return IsoCall(x => x.Begin((uint)width, (uint)height, path));
    }

    /// <summary><c>rsb_isolated_state</c> (0 idle … 5 ending, negative = error).</summary>
    internal int IsolatedState() => IsoCall(x => x.State());

    /// <summary><c>rsb_isolated_last_render</c>: drawn count, or a negative code.</summary>
    internal int IsolatedLastRender() => IsoCall(x => x.LastRender());

    internal void IsolatedRequestTechnique(string effectFile, string name, bool on)
    {
        if (EncodeZ(name) is not { } nameBytes) return;
        var effectBytes = EncodeZ(effectFile);
        IsoCall(x => { x.RequestTechnique(effectBytes, nameBytes, on ? 1 : 0); return 0; });
    }

    internal void IsolatedQueueRender(IntPtr d3d11Texture) => IsoCall(x => { x.QueueRender(d3d11Texture); return 0; });

    internal void IsolatedEnd() => IsoCall(x => { x.End(); return 0; });

    /// <summary>The isolated render-event callback for <c>GL.IssuePluginEvent</c>, or zero when unsupported.</summary>
    internal IntPtr IsolatedEventFunc()
    {
        if (!IsolatedSupported) return IntPtr.Zero;
        try
        {
            return _isolated!.EventFunc();
        }
        catch (Exception ex)
        {
            Fault(ex);
            return IntPtr.Zero;
        }
    }

    private int IsoCall(Func<IsolatedExports, int> call)
    {
        if (!IsolatedSupported) return IsolatedInternalError;
        try
        {
            return call(_isolated!);
        }
        catch (Exception ex)
        {
            Fault(ex);
            return IsolatedInternalError;
        }
    }
}
