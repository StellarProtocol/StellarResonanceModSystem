using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only access to the current party roster. Observed on the Unity main thread.
/// </summary>
public interface IPartyRoster
{
    /// <summary>
    /// All party members including self. Sorted with self first, then by join order.
    /// Empty when solo. The reference is stable for the frame; the snapshot is rebuilt
    /// lazily when state changes.
    /// </summary>
    IReadOnlyList<PartyMember> Members { get; }
}
