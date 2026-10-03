using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated per-layer logging for the visibility backend.</summary>
internal sealed partial class GameVisibilityBackend
{
    private void OnLayerSet(VisibilityLayers layer, bool hide, bool ok)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoVis] {layer} hide={hide} ok={ok} applied={_applied}");
    }

    private void OnEntityShowWritten(int type, bool show)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[PhotoVis] SetEntityShow({type},{show}) by Stellar");
    }
}
