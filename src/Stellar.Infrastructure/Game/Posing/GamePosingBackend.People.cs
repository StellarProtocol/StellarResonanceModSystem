using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>People near the local player: you first, then players (<c>CharIdList</c>) and NPCs (<c>EntityDict</c>,
/// kind 2) within the radius by distance, named in one Lua read. Called on a click — the dictionary walk allocates.
/// Empty inside the scene-change settle window.</summary>
internal sealed partial class GamePosingBackend
{
    private readonly List<long> _ids = new();
    private readonly List<(long Uuid, PersonKind Kind, float Distance)> _found = new();

    public void People(float radius, List<PersonInfo> into)
    {
        _found.Clear();
        if (_settle.Settling) return;
        var me = _entities.LocalEntity();
        if (me is null || _entities.LocalPlayerPosition() is not Vector3 origin) return;
        var self = _entities.Uuid(me);
        _found.Add((self, PersonKind.Self, 0f));
        _entities.CharIds(_ids);
        foreach (var id in _ids) AddNear(_entities.CharEntity(id), PersonKind.Player, origin, radius, self);
        _entities.EntityUuids(_ids);
        foreach (var uuid in _ids)
            if (_entities.EntityByUuid(uuid) is { } e && _entities.EntType(e) == FreezeKinds.Npc) AddNear(e, PersonKind.Npc, origin, radius, self);
        _found.Sort(CompareFound);
        var uuids = _found.ConvertAll(f => f.Uuid);
        var names = _calls.Lua.Names(uuids);
        foreach (var f in _found)
            into.Add(new PersonInfo(new EntityId(f.Uuid), names.TryGetValue(f.Uuid, out var n) ? n : "", f.Kind, f.Distance));
    }

    private void AddNear(object? entity, PersonKind kind, Vector3 origin, float radius, long self)
    {
        if (entity is null || _entities.LiveModel(entity) is not { } model || _entities.AttrPosition(model) is not Vector3 p) return;
        var uuid = _entities.Uuid(entity);
        if (uuid == self || _found.Exists(f => f.Uuid == uuid)) return;
        var d = Distance(origin, p);
        if (d <= radius) _found.Add((uuid, kind, d));
    }

    private static int CompareFound((long Uuid, PersonKind Kind, float Distance) a, (long Uuid, PersonKind Kind, float Distance) b)
    {
        if (a.Kind == PersonKind.Self) return b.Kind == PersonKind.Self ? 0 : -1;
        return b.Kind == PersonKind.Self ? 1 : a.Distance.CompareTo(b.Distance);
    }

    private static float Distance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x, dy = a.y - b.y, dz = a.z - b.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
