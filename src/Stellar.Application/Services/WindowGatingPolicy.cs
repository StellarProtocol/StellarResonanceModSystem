using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>Pure rule: a window is draw-suppressed when it opted into auto-hide
/// AND a full-screen game menu is open, OR it opted into hide-until-in-world AND
/// the player isn't logged in yet.</summary>
internal static class WindowGatingPolicy
{
    /// <summary>Primitive core — shared by the WindowSpec path and the HUD path (HudSpec is a different
    /// type, but the rule is identical).</summary>
    public static bool IsDrawSuppressed(bool autoHideBehindGameMenus, bool hideUntilInWorld, bool fullScreenMenuOpen, bool loggedIn)
        => (autoHideBehindGameMenus && fullScreenMenuOpen)
        || (hideUntilInWorld && !loggedIn);

    public static bool IsDrawSuppressed(WindowSpec spec, bool fullScreenMenuOpen, bool loggedIn)
        => IsDrawSuppressed(spec.AutoHideBehindGameMenus, spec.HideUntilInWorld, fullScreenMenuOpen, loggedIn);
}
