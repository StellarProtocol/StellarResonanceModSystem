using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound contract for reading the local player's equipped Battle Imagines
/// (<c>CharSerialize.resonance</c>, wire field 28). The Host-selected
/// implementation is <c>PandaLoadoutProbe</c>, which reads the LIVE Lua mirror
/// via its refresh chunk — the C# <c>CharSerialize</c> reflection mirror
/// (<c>PandaInventoryProbe</c>, still implemented but unwired) is a stale latch
/// that kept serving the pre-swap pair after an in-session imagine swap (owner
/// staging run <c>sea/445626427740520448</c>, 2026-08-23). Application's
/// <c>ResonanceService</c> consumes this without ever touching IL2CPP.
///
/// Returns <c>false</c> rather than throwing when the data isn't readable yet
/// (bridge unresolved / no "RES" row parsed) — Application treats this as
/// "data not ready" and keeps its last published snapshot.
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
