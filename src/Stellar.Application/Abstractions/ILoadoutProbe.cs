using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's loadout system. Implemented in Infrastructure.</summary>
internal interface ILoadoutProbe
{
    /// <summary>True once the game-side loadout bridge is resolved.</summary>
    bool IsResolved { get; }

    /// <summary>Enumerate saved loadouts (id + name). Empty when unresolved.</summary>
    IReadOnlyList<LoadoutEntry> ReadLoadouts();

    /// <summary>The current loadout id, or null if none/unknown.</summary>
    int? ReadCurrentIndex();

    /// <summary>Dispatch the native switch to <paramref name="index"/> (a loadout id).</summary>
    Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct);
}

/// <summary>A raw saved-loadout entry read from the game.</summary>
internal readonly record struct LoadoutEntry(int Index, string Name);
