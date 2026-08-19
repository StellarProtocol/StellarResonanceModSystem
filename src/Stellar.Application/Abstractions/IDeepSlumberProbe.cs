using Stellar.Abstractions.Domain.DeepSlumber;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's Deep-Slumber (season cultivate) containers.
/// Implemented in Infrastructure.</summary>
internal interface IDeepSlumberProbe
{
    /// <summary>True once the live CharSerialize container is reachable.</summary>
    bool IsResolved { get; }

    /// <summary>Read the full live state, or null when unresolved / not yet synced.</summary>
    DeepSlumberState? Read();
}
