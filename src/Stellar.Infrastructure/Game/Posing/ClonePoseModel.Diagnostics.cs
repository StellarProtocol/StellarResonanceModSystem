using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Diagnostics only (STELLAR_DIAGNOSTICS=1): on close, whether the real player was set visible again — the
/// <c>SetVisible(true)</c> result (<c>True</c>/<c>False</c>), <c>gone</c> when the player already left, <c>not-hidden</c>
/// when the open never hid them (review I-2; proof report concern 1). A throwing call is warned by the close step.</summary>
internal sealed partial class ClonePoseModel
{
    partial void OnPlayerShown(string shown)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _info($"[Posing] photo copy closed playerVisibleAgain={shown}");
    }
}
