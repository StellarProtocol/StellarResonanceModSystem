using System.Collections.Generic;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only view of the local player's equipped Battle Imagines, read from the
/// skill hotbar's aoyi slots (the game's <c>Slot</c> container, slots 7/8 — the
/// only representation the game re-serializes on an in-session swap). The
/// equipped set drives the CombatMeter's setup-identity Imagine pair. Populated
/// on the game main thread and published as an immutable snapshot so reads are
/// lock-free.
/// </summary>
public interface IResonanceState
{
    /// <summary>
    /// The equipped Battle Imagines' aoyi SKILL ids, in canonical slot order
    /// (<c>[0]</c> = hotbar slot 7, <c>[1]</c> = hotbar slot 8; a slot with no
    /// imagine is omitted). Empty until the first successful read. Each id
    /// resolves to display + cooldown data via
    /// <see cref="IGameDataResonance"/> (<c>GetImagineForSkill</c>).
    /// </summary>
    IReadOnlyList<int> Installed { get; }
}
