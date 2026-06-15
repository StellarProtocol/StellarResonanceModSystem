using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only view of the local player's equipped Battle Imagines (Resonance
/// Skills), decoded from <c>CharSerialize.resonance</c> (wire field 28). The
/// equipped set drives the CombatMeter's trailing Imagine icons for the self
/// row. Populated on the network receive thread and read on the main thread;
/// implementations publish an immutable snapshot so reads are lock-free.
/// </summary>
public interface IResonanceState
{
    /// <summary>
    /// The equipped Imagine resonance ids, in slot order
    /// (<c>[0]</c> = left / X, <c>[1]</c> = right / Z). Empty until a container
    /// sync has populated it. Each id resolves to display + cooldown data via
    /// <see cref="IGameDataResonance"/>.
    /// </summary>
    IReadOnlyList<int> Installed { get; }
}
