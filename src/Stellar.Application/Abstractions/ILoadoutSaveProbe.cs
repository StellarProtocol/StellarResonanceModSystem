using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Application.Abstractions;

/// <summary>Outbound boundary for the game's loadout SAVE (Role Plan <c>AsyncSaveRolePlan</c>) and its
/// "unsaved changes" check. Implemented in Infrastructure by the same probe that reads + switches
/// loadouts (<see cref="ILoadoutProbe"/>), because it shares that probe's Lua bridge, main-thread drain and
/// in-flight switch state. Kept as its own interface so <see cref="ILoadoutProbe"/> does not grow.</summary>
internal interface ILoadoutSaveProbe
{
    /// <summary>The cached result of the game's <c>CheckRolePlanIsChange</c>; false until first read.</summary>
    bool HasUnsavedChanges { get; }

    /// <summary>Dispatch the game's save of the worn setup into <paramref name="index"/> (a loadout id).
    /// Refuses without dispatching when the target is the worn loadout, unknown, or a switch/save is in
    /// flight.</summary>
    Task<LoadoutResult> CallSaveAsync(int index, CancellationToken ct);
}
