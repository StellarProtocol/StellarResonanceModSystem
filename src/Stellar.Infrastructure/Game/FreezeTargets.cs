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

/// <summary>Who the freeze's position hold may pin — the wiring between the entity reads and <see cref="FreezeLedger"/>, kept
/// pure so it is unit-tested with a fake source (review I2). Never held (review I1): the local player and their OWN mount —
/// the mount they ride, or one they drive. Read once per press: the time pause stops everyone, so nobody can ride up or
/// summon a mount mid-freeze.</summary>
internal static class FreezeTargets
{
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

    /// <summary>A mount is the local player's when they ride it or drive it. Unknown values (0) never match.</summary>
    public static bool IsOwnMount(long vehicle, long controller, long self, long ridden) =>
        vehicle != 0 && (vehicle == ridden || (self != 0 && controller == self));

    /// <summary>The hold runs only for a press that knows the local player (review M1): with the player's uuid unread (0),
    /// nobody is excluded yet, so holding anyone could pin the player themself.</summary>
    public static bool MayHoldAny(FreezeLedger ledger) => ledger.Self != 0;

    /// <summary>Hold admission: a movable kind within the hold radius, not excluded, not already held.</summary>
    public static bool MayHold(FreezeLedger ledger, long uuid, int kind, float distance, bool alreadyHeld) =>
        !ledger.Excludes(uuid) && FreezeKinds.Movable(kind) && distance <= FreezeKinds.HoldRadius && !alreadyHeld;
}
