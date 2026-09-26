using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostic sibling partial for the loadout SAVE path + the unsaved-changes flag of
/// <see cref="PandaLoadoutProbe"/>. Every line is gated on <see cref="StellarDiagnostics.IsEnabled"/>;
/// the save's one always-on outcome line lives with the save itself (a user action).</summary>
internal sealed partial class PandaLoadoutProbe
{
    private void DiagSaveDispatched(int planId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] AsyncSaveRolePlan(planId={planId}) dispatched (worn={_liveCurrentPlanId})");
    }

    private void DiagUnsavedChanged(bool value)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] unsaved changes = {value} (CheckRolePlanIsChange)");
    }

    private void DiagUnsavedCheckFailed(string? raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] CheckRolePlanIsChange gave no answer: {raw ?? "(unset)"}");
    }
}
