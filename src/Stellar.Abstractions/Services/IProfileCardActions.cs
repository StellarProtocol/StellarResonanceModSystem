using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Plugin-facing service for contributing buttons to the game's native profile card action bar.
/// Each registered <see cref="ProfileCardActionSpec"/> becomes a styled button the framework
/// injects into the card on open; the returned handle removes the action when disposed.
/// </summary>
public interface IProfileCardActions
{
    /// <summary>Register an action button. Returns a handle; dispose it to remove the action.</summary>
    IDisposable Register(ProfileCardActionSpec spec);
}
