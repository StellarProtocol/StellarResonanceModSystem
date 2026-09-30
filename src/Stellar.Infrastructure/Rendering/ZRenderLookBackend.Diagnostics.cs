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

    partial void OnFocusWritten(float distance)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoLook] focus {LookParameterPlan.FocusField}={distance:F2}");
    }
}
