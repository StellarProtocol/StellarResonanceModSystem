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

    private void OnPumpStopped(int livePumps)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] resume served on thread {System.Environment.CurrentManagedThreadId}; pump stopped, live pumps={livePumps}");
    }

    private void OnTickDrain(int completed)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoCapture] tick drained {completed} late resume(s) on thread {System.Environment.CurrentManagedThreadId}");
    }
}
