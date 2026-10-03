using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Rendering;

/// <summary>StellarDiagnostics-gated ReShade logging, capped at <see cref="DiagnosticLineCap"/> lines per session.</summary>
internal sealed partial class ReShadeService
{
    internal const int DiagnosticLineCap = 200;

    private int _diagnosticLines;

    private void OnBound() => Diag("bound");

    private void OnTechniquesRead(int count)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Diag($"techniques={count} available={_available}");
    }

    private void OnPresetSeen(string? preset)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Diag($"preset={preset ?? "<none>"}");
    }

    private void OnRequest(string what, string detail)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Diag($"request {what} {detail}");
    }

    private void OnHeld(string what, string detail)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Diag($"held {what} {detail} (sent once the add-on is bound and lists techniques)");
    }

    private void Diag(string message)
    {
        if (!StellarDiagnostics.IsEnabled || _diagnosticLines > DiagnosticLineCap) return;
        _diagnosticLines++;
        _log.Info(_diagnosticLines > DiagnosticLineCap
            ? "[ReShade] diagnostics cap reached; further lines dropped"
            : $"[ReShade] {message}");
    }
}
