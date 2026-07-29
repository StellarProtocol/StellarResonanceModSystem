namespace Stellar.Abstractions.Domain;

/// <summary>
/// Coarse client lifecycle phase — a first-class, framework-owned signal distinct from
/// session state (<see cref="Services.IClientState.IsLoggedIn"/>). A plugin reads it to decide
/// window visibility (via <see cref="IRenderGated.ShouldRender"/>), e.g. a gameplay window that
/// only draws in <see cref="World"/>, or a login-screen tool that draws in <see cref="TitleScreen"/>.
///
/// <para>Plain single-value enum (append-friendly — new phases like a future <c>CharSelect</c> can be
/// added without breaking existing checks). The framework <b>gates nothing</b> on this value; it is a
/// signal a plugin reads. The only protective gate is <see cref="Services.IClientState.IsWorldActive"/>.</para>
/// </summary>
public enum GamePhase
{
    /// <summary>Boot, title, login, and character-select — anything before the player is in a world scene.</summary>
    TitleScreen,

    /// <summary>The player is in a world scene. Stays <see cref="World"/> across in-world zone loads
    /// (unlike <see cref="Services.IClientState.IsWorldActive"/>, which dips false mid-transition).</summary>
    World,
}
