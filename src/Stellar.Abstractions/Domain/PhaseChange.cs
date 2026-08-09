using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Payload for <see cref="IClientState.PhaseChanged"/>. Carries both ends of the transition so a plugin
/// needn't track the previous phase itself.
/// </summary>
/// <param name="From">The phase before the transition.</param>
/// <param name="To">The phase after the transition.</param>
public readonly record struct PhaseChange(GamePhase From, GamePhase To);
