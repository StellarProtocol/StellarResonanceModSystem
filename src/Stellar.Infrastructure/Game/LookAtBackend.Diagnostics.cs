using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the look-at backend.</summary>
internal sealed partial class LookAtBackend
{
    partial void OnApplied(LookAtSnapshot pre)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] look-at on; snapshot enable={pre.Enable} head={pre.Head} eye={pre.Eye}");
    }

    partial void OnRestored(int corrections)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] look-at restored; corrective writes={corrections}");
    }
}
