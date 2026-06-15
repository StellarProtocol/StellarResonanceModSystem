using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Combat event stream. Mirrors the event half of the original mixed combat
/// surface; polled state lives on <see cref="ICombatSnapshot"/> and per-entity
/// lookups on <see cref="ICombatLookup"/>. Events always fire on the Unity
/// main thread (drained once per <c>Game.Update</c> postfix).
/// </summary>
public interface ICombatEvents
{
    /// <summary>Fires on the main (Unity) thread once per game tick during drain.</summary>
    event Action<CombatEvent> CombatEventOccurred;
}
