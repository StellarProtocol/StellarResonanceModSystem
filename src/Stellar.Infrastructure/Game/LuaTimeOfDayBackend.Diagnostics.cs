using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the time-of-day backend.</summary>
internal sealed partial class LuaTimeOfDayBackend
{
    partial void OnWritten(string what, object value)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoTime] {what} {value}");
    }

    partial void OnGameCallObserved(string method)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoTime] game call {method} → re-assert pending");
    }
}
