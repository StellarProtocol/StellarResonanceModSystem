using UnityEngine;

namespace Stellar.Infrastructure.Unity;

public sealed partial class WindowInteractionTicker
{
    // pixelPerfect snaps graphics to the physical pixel grid (crisp text), but it re-snaps every frame — so a
    // MOVING window's contents step by whole pixels instead of gliding. Turn it off while a window is being
    // dragged or resized (smooth sub-pixel movement), back on otherwise so static UI stays crisp. Guarded so the
    // canvas rebuild only happens when the state actually flips.
    private void SyncPixelPerfect()
    {
        if (_canvasComp == null) _canvasComp = GetComponent<Canvas>();
        if (_canvasComp == null) return;
        // pixelPerfect ON keeps glyphs/graphics on whole pixels (crisp text) but re-snaps every frame, which makes
        // a MOVING window's contents step by whole pixels — so turn it off at ANY scale while a window is being
        // dragged or resized (smooth sub-pixel drag), then back on so static UI re-crisps on release.
        var moving = _activeWinDrag != null || _activeResize >= 0;
        var want = !moving;
        if (_canvasComp.pixelPerfect != want) _canvasComp.pixelPerfect = want;
    }
}
