using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>The game reads the freeze's target selection needs (implemented by <see cref="GameEntityAccess"/>; faked in
/// unit tests).</summary>
internal interface IFreezeEntitySource
{
    /// <summary>Every entity uuid in the scene (once per press).</summary>
    void EntityUuids(List<long> into);

    /// <summary>The local player's uuid, 0 when unknown.</summary>
    long PlayerUuid();

    /// <summary>The mount entity the local player rides, 0 when none.</summary>
    long RiddenVehicle();

    /// <summary>The game kind of an entity (<see cref="FreezeKinds"/>), −1 when unknown.</summary>
    int Kind(long uuid);

    /// <summary>The entity driving the mount <paramref name="uuid"/>, 0 when none / unknown.</summary>
    long VehicleController(long uuid);
}

/// <summary>Who a freeze touches — the wiring between the entity reads and <see cref="FreezeLedger"/>, kept pure so it is
/// unit-tested with a fake source (review I2). Never frozen (scene-stays spec § 3, review I1): the local player and their
/// OWN mount — the mount they ride, or one they drive (a mount summoned mid-freeze is a new entity, checked as it
/// appears and again for <see cref="FreezeTargets.MountWatchFrames"/> late frames while the ride-up settles).</summary>
internal static class FreezeTargets
{
    /// <summary>Late frames an appearing mount is re-checked for being the local player's (ride-up can set the link after
    /// the mount appears). Bounded and appear-triggered — not a poll.</summary>
    internal const int MountWatchFrames = 300;

    /// <summary>The press: reads every uuid, starts the ledger with the local player, excludes their own mount, and drops
    /// every excluded entity from <paramref name="ids"/>.</summary>
    public static void Select(IFreezeEntitySource src, FreezeLedger ledger, List<long> ids)
    {
        src.EntityUuids(ids);
        ledger.Begin(src.PlayerUuid());
        var ridden = src.RiddenVehicle();
        ledger.Exclude(ridden);
        foreach (var id in ids)
            if (src.Kind(id) == FreezeKinds.Vehicle && IsOwnMount(id, src.VehicleController(id), ledger.Self, ridden)) ledger.Exclude(id);
        ledger.WithoutSelf(ids);
    }

    /// <summary>Stage 2's re-check: learns the local player if the press read 0 (review M1) and excludes the mount they
    /// now ride. Adds every entity newly excluded to <paramref name="released"/> — the caller undoes what the press froze
    /// on them.</summary>
    public static void Recheck(IFreezeEntitySource src, FreezeLedger ledger, List<long> released)
    {
        released.Clear();
        var self = src.PlayerUuid();
        if (ledger.LearnSelf(self)) released.Add(self);
        var ridden = src.RiddenVehicle();
        if (ledger.Exclude(ridden)) released.Add(ridden);
    }

    /// <summary>An entity appearing while frozen: false (freeze nothing) for an excluded entity, the local player, or
    /// the local player's own mount (which this call excludes). <paramref name="controller"/> is the mount's driver
    /// read from the hook's own entity (0 for other kinds).</summary>
    public static bool AdmitAppeared(IFreezeEntitySource src, FreezeLedger ledger, long uuid, int kind, long controller)
    {
        if (ledger.Excludes(uuid) || uuid == src.PlayerUuid()) return false;
        return !ExcludeIfOwnMount(src, ledger, uuid, kind, controller);
    }

    /// <summary>True — and <paramref name="uuid"/> is now excluded — when it is a mount the local player rides or drives.</summary>
    public static bool ExcludeIfOwnMount(IFreezeEntitySource src, FreezeLedger ledger, long uuid, int kind, long controller)
    {
        if (kind != FreezeKinds.Vehicle) return false;
        var self = ledger.Self != 0 ? ledger.Self : src.PlayerUuid();
        if (!IsOwnMount(uuid, controller, self, src.RiddenVehicle())) return false;
        ledger.Exclude(uuid);
        return true;
    }

    /// <summary>A mount is the local player's when they ride it or drive it. Unknown values (0) never match.</summary>
    public static bool IsOwnMount(long vehicle, long controller, long self, long ridden) =>
        vehicle != 0 && (vehicle == ridden || (self != 0 && controller == self));

    /// <summary>Hold admission: a movable kind within the hold radius, not excluded, not already held.</summary>
    public static bool MayHold(FreezeLedger ledger, long uuid, int kind, float distance, bool alreadyHeld) =>
        !ledger.Excludes(uuid) && FreezeKinds.Movable(kind) && distance <= FreezeKinds.HoldRadius && !alreadyHeld;
}
