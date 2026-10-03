using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game;

internal sealed partial class GameEffectVisibility
{
    private const int MaxClassifiedLines = 400;
    private int _classifiedLines;

    partial void OnClassified(long uid, long from, long belong, VisibilityLayers owner)
    {
        if (!StellarDiagnostics.IsEnabled || owner != VisibilityLayers.None || _classifiedLines >= MaxClassifiedLines) return;
        if (from == 0 && belong == 0) return;   // scenery: expected, not interesting
        _classifiedLines++;
        _log.Info($"[EffectHide] unresolved uid={uid} from={from} belong={belong}");
    }

    partial void OnSwept(VisibilityLayers wanted, int hidden, int shown, int held)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[EffectHide] apply wanted={wanted} hidden={hidden} shown={shown} held={held}");
    }
}
