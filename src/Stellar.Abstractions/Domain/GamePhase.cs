namespace Stellar.Abstractions.Domain;

/// <summary>
/// Coarse client lifecycle phase — a first-class, framework-owned signal distinct from
/// session state (<see cref="Services.IClientState.IsLoggedIn"/>). A plugin reads it to decide
/// window visibility (via <see cref="IRenderGated.ShouldRender"/>), e.g. a gameplay window that
/// only draws in <see cref="World"/>, or a login-screen tool that draws in <see cref="TitleScreen"/>.
///
/// <para>Ordered by lifecycle (<see cref="Startup"/> → <see cref="TitleScreen"/> → <see cref="CharSelect"/>
/// → <see cref="World"/>). The values are a runtime signal only — nothing persists, serializes, or wires them,
/// so the members may be re-ordered or inserted without a compatibility break. The framework <b>gates
/// nothing</b> on this value; it is a signal a plugin reads. The only protective gate is
/// <see cref="Services.IClientState.IsWorldActive"/>.</para>
/// </summary>
public enum GamePhase
{
    /// <summary>Boot / loading, before the login UI exists — the INITIAL phase at process start. A
    /// login-screen tool must NOT draw here (the login view isn't up yet). The framework latches
    /// <see cref="Startup"/> → <see cref="TitleScreen"/> once it detects the game's login view active.</summary>
    Startup,

    /// <summary>The login screen is actually up (the game's <c>login_main</c> view is active). Entered —
    /// and <b>latched</b> (never flickers back to <see cref="Startup"/>) — when the framework detects that
    /// view. A login-screen tool (account switcher, server picker) targets this phase alone.</summary>
    TitleScreen,

    /// <summary>The character-select screen. Entered on the game's <c>OnLogin</c> event (which
    /// empirically fires when char-select appears — <see cref="Services.IClientState.IsLoggedIn"/> becomes
    /// true there, NOT at world-connect). The Unity scene name does not change between title and char-select,
    /// so this phase is the only signal that distinguishes them. Cancelling back to title fires <c>OnLogout</c>
    /// (<see cref="TitleScreen"/>).</summary>
    CharSelect,

    /// <summary>The player is in a world scene. Stays <see cref="World"/> across in-world zone loads
    /// (unlike <see cref="Services.IClientState.IsWorldActive"/>, which dips false mid-transition).</summary>
    World,
}
