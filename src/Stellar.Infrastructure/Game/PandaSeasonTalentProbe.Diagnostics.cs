using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaSeasonTalentProbe"/>. Per-op dispatch/result lines
/// are gated on <see cref="StellarDiagnostics.IsEnabled"/>; the one-shot bridge-resolution line fires
/// unconditionally so scenario gates have evidence the Lua bridge resolved even on a non-diagnostic
/// run.
/// </summary>
internal sealed partial class PandaSeasonTalentProbe
{
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60;

    // Always-on one-shot: proves the Lua-bridge reflection targets resolved and names the driving
    // path (Approach A — raw worldProxy RPCs, not the season_talent VM wrapper).
    private void OnResolutionSucceeded()
    {
        _log.Info(
            "[Stellar][SeasonTalent] resolved write bridge: tolua# LuaState.mainState + DoString" +
            "; driving raw worldProxy RPCs (Approach A)");
    }

    private void OnResolutionFailure(string reason)
    {
        _failedResolutionAttempts++;
        if (!_resolutionFailureLogged)
        {
            _resolutionFailureLogged = true;
            _log.Warning($"[Stellar][SeasonTalent] bridge not resolved: {reason}");
            return;
        }
        if (_failedResolutionAttempts % ResolutionFailureLogEvery == 0)
        {
            _log.Warning($"[Stellar][SeasonTalent] bridge still not resolved ({_failedResolutionAttempts} attempts): {reason}");
        }
    }

    private void DiagDispatched(int opId, string chunk)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][SeasonTalent] op {opId} dispatched: {chunk}");
    }

    private void DiagResult(int opId, int code, double elapsedMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][SeasonTalent] op {opId} result: code={code} after {elapsedMs:F0}ms");
    }
}
