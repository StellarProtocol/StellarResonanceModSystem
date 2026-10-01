using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated logging for the look backend (per-write lines are too chatty for normal play).</summary>
internal sealed partial class ZRenderLookBackend
{
    partial void OnVolumeCreated()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoLook] volume created isGlobal=true priority={VolumePriority} weight=1");
    }

    partial void OnParamWritten(ParamWrite write, object value)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoLook] write {write.Component}.{write.Field}={value}");
    }

    // For the in-game grain probe: what texture the grain pass is fed (the per-field grain writes log via OnParamWritten).
    partial void OnGrainTextureCreated(int size, byte[] pixels)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        long sum = 0;
        for (var i = 3; i < pixels.Length; i += 4) sum += pixels[i];
        var mean = sum / (double)(pixels.Length / 4) / 255.0;
        _log.Info($"[PhotoLook] grain texture created {size}x{size} RGBA32 linear Repeat mean={mean:F3}");
    }

    partial void OnFocusWritten(float distance)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoLook] focus {LookParameterPlan.FocusField}={distance:F2}");
    }
}
