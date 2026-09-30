using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated capture logging.</summary>
internal sealed partial class UnityFrameGrabber
{
    private void OnReadback(string path, int bytes, int expected)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] readback via {path}: {bytes} bytes (expected {expected})");
    }
}
