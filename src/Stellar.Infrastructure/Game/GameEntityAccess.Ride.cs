using System;
using System.Reflection;
namespace Stellar.Infrastructure.Game;

/// <summary>The rider ↔ mount link, for the freeze's own-mount exclusion (review I1). The game keeps it on two attrs read
/// through <c>EntityAttrExtensions</c> (release_3.7 interop, decompiled with ilspycmd; ids from StarResonanceData
/// <c>enum_e_attr_type.proto</c>): the rider's <c>GetAttrRideUuid</c> (<c>AttrRideUuid</c> 605 — the mount entity being
/// ridden) and the mount's <c>GetVehicleController</c> (<c>AttrVehicleController</c> — the entity driving it). Either read
/// fails soft to 0 (unknown) — a kind without the component throws <c>MethodAccessException</c>, as the factor attr does
/// (recon run 3). One-shot reads on a freeze press, an appearing mount, or the game's vehicle ride event — never per
/// frame.</summary>
internal sealed partial class GameEntityAccess : IFreezeEntitySource
{
    private MethodInfo? _rideUuid, _vehicleController;
    private bool _rideResolved;

    /// <summary>The mount entity the local player rides (<c>AttrRideUuid</c>), or 0 when not riding / unreadable.</summary>
    public long RiddenVehicle() => LocalEntity() is { } me && ResolveRide() ? ReadLong(_rideUuid, me) : 0L;

    /// <summary>The entity driving the mount <paramref name="vehicle"/> (<c>AttrVehicleController</c>), or 0.</summary>
    public long VehicleController(object vehicle) => ResolveRide() ? ReadLong(_vehicleController, vehicle) : 0L;

    /// <summary>The driver of the mount with uuid <paramref name="uuid"/>, or 0 when gone / unreadable.</summary>
    public long VehicleController(long uuid) => EntityByUuid(uuid) is { } e ? VehicleController(e) : 0L;

    /// <summary>The game kind of <paramref name="uuid"/> (<see cref="FreezeKinds"/>), or −1 when gone / unreadable.</summary>
    public int Kind(long uuid) => EntityByUuid(uuid) is { } e ? EntType(e) : -1;

    private bool ResolveRide()
    {
        if (_rideResolved) return _rideUuid is not null && _vehicleController is not null;
        var ext = _types.FindType(AttrExtType);
        var ent = _types.FindType(EntityType);
        if (ext is null || ent is null) return false;
        _rideUuid = ext.GetMethod("GetAttrRideUuid", BindingFlags.Public | BindingFlags.Static, null, new[] { ent }, null);
        _vehicleController = ext.GetMethod("GetVehicleController", BindingFlags.Public | BindingFlags.Static, null, new[] { ent }, null);
        _rideResolved = true;
        return _rideUuid is not null && _vehicleController is not null;
    }

    private long ReadLong(MethodInfo? method, object entity)
    {
        if (method is null) return 0L;
        try
        {
            _arg1[0] = entity;
            return Convert.ToInt64(method.Invoke(null, _arg1));
        }
        catch { return 0L; }
    }
}
