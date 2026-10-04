using System;
using System.Runtime.InteropServices;

namespace Stellar.Infrastructure.Rendering;

/// <summary>The add-on's OPTIONAL uniform-override exports (bridge 1.1.0+, README § "Uniform overrides"), detected
/// with <c>GetProcAddress</c> like the isolated capture (<c>rsb_version()</c> stays 1). The add-on keeps the overrides
/// process-wide and re-applies them itself after reloads, preset switches and runtime re-creation (window resize).</summary>
internal sealed partial class ReShadeBridge : IReShadeUniformNative
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUniformFn(byte[]? effect, byte[] variable, byte[]? value);

    private sealed class UniformExports
    {
        internal SetUniformFn Set = null!;
        internal VoidFn Clear = null!;
    }

    private UniformExports? _uniforms;

    public bool UniformOverridesSupported => TryBind() && _uniforms is not null;

    private static UniformExports? TryBindUniforms(IntPtr module)
    {
        try
        {
            return new UniformExports
            {
                Set = Bind<SetUniformFn>(module, "rsb_set_uniform_override"),
                Clear = Bind<VoidFn>(module, "rsb_clear_uniform_overrides"),
            };
        }
        catch (Exception)
        {
            return null;   // a 1.0.0 add-on: no uniform overrides
        }
    }

    public int SetUniformOverride(string? effectFile, string variable, string? value)
    {
        if (!UniformOverridesSupported || EncodeZ(variable) is not { } name) return 0;
        var effect = EncodeZ(effectFile);
        var text = EncodeZ(value);
        try
        {
            return _uniforms!.Set(effect, name, text);
        }
        catch (Exception ex)
        {
            Fault(ex);
            return 0;
        }
    }

    public void ClearUniformOverrides()
    {
        if (!UniformOverridesSupported) return;
        try
        {
            _uniforms!.Clear();
        }
        catch (Exception ex)
        {
            Fault(ex);
        }
    }
}
