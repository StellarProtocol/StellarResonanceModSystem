using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

// Gated dropdown diagnostics (STELLAR_DIAGNOSTICS=1), zero cost otherwise. The popup's open geometry can't be
// sandbox-verified (Mono ≠ IL2CPP for canvas/positioning/raycast), so log the measured trigger anchor + the
// resolved popup rect IN-GAME — so positioning is MEASURED rather than eyeballed (the project UI-verification
// rule). Sibling to WindowBuilder.Dropdown.cs so the logic partial stays free of inline diagnostics blocks.
internal sealed partial class WindowBuilder
{
    // Logs the trigger anchor, option count, and the popup's resolved top-left + size against the screen, so a
    // mis-positioned / clipped / flipped popup can be diagnosed from the log without a screenshot.
    private static void DropdownOpenedDiag(WindowRect anchor, int options, GameObject? panel)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var rt = panel != null ? panel.GetComponent<RectTransform>() : null;
        var pos = rt != null ? rt.position : default;
        var size = rt != null ? rt.rect.size : default;
        Debug.Log($"[Dropdown/Diag] open anchor=({anchor.X:0},{anchor.Y:0} {anchor.Width:0}x{anchor.Height:0}) "
                + $"options={options} popupTopLeft=({pos.x:0},{pos.y:0}) popupSize=({size.x:0}x{size.y:0}) "
                + $"screen=({Screen.width}x{Screen.height})");
    }

    // A plugin's OnSelect threw — swallowed so a bad callback can't break the overlay, surfaced only when diag is on.
    private static void DropdownPickThrewDiag(Exception ex)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        Debug.LogWarning($"[Dropdown/Diag] OnSelect callback threw: {ex.Message}");
    }
}
