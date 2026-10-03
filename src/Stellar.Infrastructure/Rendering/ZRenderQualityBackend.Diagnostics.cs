using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated logging for the render-quality backend (per-write / per-hook lines).</summary>
internal sealed partial class ZRenderQualityBackend
{
    partial void OnLeverWritten(string lever, object value)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoQuality] write {lever}={value}");
    }

    partial void OnGameApplyObserved(string method)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoQuality] game apply {method} → re-assert pending");
    }
}
