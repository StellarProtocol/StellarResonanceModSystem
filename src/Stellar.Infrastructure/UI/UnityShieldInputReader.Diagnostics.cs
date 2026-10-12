using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.UI;

/// <summary>StellarDiagnostics-gated logging for the over-game-UI raycast: one line each time the answer flips, naming the
/// top-most hit (object, sorting order) so an in-game test shows what the game's interface answers with.</summary>
internal sealed partial class UnityShieldInputReader
{
    private bool _loggedOver;

    partial void OnGameUiProbe(bool over)
    {
        if (!StellarDiagnostics.IsEnabled || over == _loggedOver) return;
        _loggedOver = over;
        var top = _uiHits.Count > 0 ? _uiHits[0] : default;
        var name = _uiHits.Count > 0 && top.gameObject != null ? top.gameObject.name : "-";
        _log.Info($"[FreeCam] pointer over game UI={(over ? "yes" : "no")} hits={_uiHits.Count} top={name} order={top.sortingOrder}");
    }
}
