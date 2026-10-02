using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Diagnostics only (STELLAR_DIAGNOSTICS=1): what the clone guard read on the source and whether it normalised
/// the ride template (regression <c>clone-nre-male-null-ridetpl</c>), and the game's registered-model count around each
/// copy (a copy adds 1; a failed copy must not leave one behind).</summary>
internal sealed partial class PhotoCopyMaker
{
    partial void OnGuardRead(CloneGuardReading r)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Posing] clone guard gender={r.Gender} state={r.State} actionId={r.ActionId} " +
                  $"rideTplNull={r.TemplateNull} normalised={r.Normalised}");
    }

    partial void OnCloning()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _actions.DiagnosticsTo(_log.Info);
        _log.Info($"[Posing] photo copy start modelDict_={_actions.ModelCount()}");
    }

    partial void OnCloned()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Posing] photo copy end modelDict_={_actions.ModelCount()}");
    }
}
