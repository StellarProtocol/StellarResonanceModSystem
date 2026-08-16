using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic-mode logging for <see cref="PandaTeamInfoRefreshProbe"/>. Both entry points short-circuit
/// on <see cref="StellarDiagnostics.IsEnabled"/> so the production partial calls them unconditionally
/// (per coding-standards § Diagnostics; same pattern as the other <c>*.Diagnostics.cs</c> partials).
/// The <c>_log.Warning</c> calls in the production partial are deliberately UNGATED — a Lua-dispatch
/// failure is rare and worth surfacing without diagnostics on.
/// </summary>
internal sealed partial class PandaTeamInfoRefreshProbe
{
    private void LogRequested(long runId, int attempt)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][TeamRefresh] GetTeamInfo requested (run={runId}, attempt {attempt}/{MaxPerRun}) — party id was 0");
    }

    private void LogBridgeResolved()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info("[Stellar][TeamRefresh] Lua bridge resolved (WorldProxy.GetTeamInfo ready)");
    }
}
