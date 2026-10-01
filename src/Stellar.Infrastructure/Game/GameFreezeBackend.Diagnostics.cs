using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the freeze backend.</summary>
internal sealed partial class GameFreezeBackend
{
    partial void OnFrozen(int effects, int factors, int held)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze on: effects={effects} entities={_ids.Count} attrFrozen={factors} positionsHeld={held}");
    }

    partial void OnStage2(int drawnFrozen)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze stage 2: drawn speed frozen={drawnFrozen}");
    }

    partial void OnAppearFrozen(long uuid, int kind)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] appeared while frozen: uuid={uuid} kind={kind}");
    }

    partial void OnUnfrozen(int effects, int factors, int speeds)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[FreeCam] freeze off: effects unfrozen={effects} factors restored={factors} drawn speeds restored={speeds}");
    }
}
