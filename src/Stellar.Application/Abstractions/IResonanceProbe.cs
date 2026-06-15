using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound contract for reading the local player's equipped Battle Imagines
/// from the game. Implemented by Infrastructure (<c>PandaInventoryProbe</c>) by
/// walking <c>CharSerialize.resonance</c> (wire field 28) on the same live
/// <c>CharSerialize</c> the inventory probe already latches. Application's
/// <c>ResonanceService</c> consumes this without ever touching IL2CPP.
///
/// Returns <c>false</c> rather than throwing when the game's container path
/// can't be resolved yet — Application treats this as "data not ready".
/// </summary>
internal interface IResonanceProbe
{
    /// <summary>
    /// Reads the equipped Imagine resonance ids in slot order. Returns
    /// <c>false</c> (and leaves <paramref name="installed"/> empty) when the
    /// live <c>CharSerialize</c> isn't resolvable yet.
    /// </summary>
    bool TryReadInstalled(out IReadOnlyList<int> installed);
}
