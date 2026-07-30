namespace Stellar.Abstractions.Domain;

/// <summary>Curated game-UI regions a mod element may be injected into.</summary>
public enum NativeUiAnchor
{
    /// <summary>The Main Menu vertical side-rail (Settings/Friends/Mail…).</summary>
    MainMenuRail = 0,
    /// <summary>An always-on HUD container in the top-right of the world HUD.</summary>
    HudTopRight = 1,
    /// <summary>The title-screen login sidebar (the vertical button column next to Settings/Switch-account).
    /// Only exists while the login view is up, so injecting here is inherently title-screen-only.</summary>
    LoginSidebar = 2,
}
