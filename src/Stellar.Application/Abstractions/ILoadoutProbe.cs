using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
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

    /// <summary>The local player's live class + talents from the LIVE line, or null when the live
    /// read has not resolved yet.</summary>
    LiveLoadoutState? ReadLiveState();

    /// <summary>Consumes the probe's "the live re-read changed what we serve" flag (equipped
    /// gear/module slots, class, talent stage/nodes, or the equipped imagine pair). Returns true at
    /// most once per real change — the probe raises it only on a structural difference, never on an
    /// identical re-parse. Read on the game tick right after <c>DrainPendingCompletions</c>, so the
    /// per-class resolve for that change has already run.</summary>
    bool ConsumeLiveStateChanged();

    /// <summary>Dispatch the native switch to <paramref name="index"/> (a loadout id).</summary>
    Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct);
}

/// <summary>A raw saved-loadout entry read from the game.</summary>
/// <param name="Index">The game's loadout/project id.</param>
/// <param name="Name">Display name as shown in the in-game dropdown.</param>
/// <param name="ProfessionId">The project's class/profession id, or 0 if unresolved
/// (e.g. a stale in-flight read still carrying the pre-enrichment 2-column form).</param>
/// <param name="TalentStageId">The project's active talent-stage config id, or 0 if
/// unresolved.</param>
/// <param name="TalentNodes">The profession's actual allocated talent-tree node ids, or null
/// if unresolved (old 4-column read / no nodes).</param>
internal readonly record struct LoadoutEntry(
    int Index,
    string Name,
    int ProfessionId = 0,
    int TalentStageId = 0,
    IReadOnlyList<int>? TalentNodes = null,
    IReadOnlyList<GearInstance>? Gear = null,
    IReadOnlyDictionary<int, ModuleInfo>? Modules = null);
