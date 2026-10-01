using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection access to the game's entities for the free-camera services: the local player, the character id list,
/// every entity (<c>EntityDict</c>, for the freeze — recon run 3), entity kind, entity → model, and model positions. Every read re-fetches through the game's null-returning lookups and skips
/// <c>IsDestroying</c> objects (docs/il2cpp-probing-safety.md); nothing is cached across frames except member handles.
/// Handles resolve lazily and retry until all are found (the hot-update assemblies load after construction). Main thread.
/// </summary>
internal sealed class GameEntityAccess
{
    internal const string ManagerType = "Panda.ZGame.ZEntityMgr";
    internal const string EntityType = "Panda.ZGame.ZEntity";
    internal const string ModelType = "Panda.ZGame.ZModel";
    internal const string AttrExtType = "Panda.ZGame.EntityAttrExtensions";
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private readonly IGameTypeRegistry _types;
    private readonly SingletonAccess _manager = new();
    private readonly object[] _arg1 = new object[1];
    private PropertyInfo? _playerUuid, _charIds, _entityDict, _model, _entDestroying, _entUuid, _entType, _modelDestroying;
    private MethodInfo? _getEntity, _getCharEntity, _chest, _attrPos;

    public GameEntityAccess(IGameTypeRegistry types) => _types = types;

    public object? Manager() => Resolve() ? _manager.Get() : null;

    public long PlayerUuid() => Manager() is { } m ? Convert.ToInt64(_playerUuid!.GetValue(m)) : 0L;

    public object? LocalEntity() => EntityByUuid(PlayerUuid());

    public object? EntityByUuid(long uuid) =>
        uuid != 0 && Manager() is { } m ? Live(Invoke1(_getEntity!, m, uuid)) : null;

    public object? CharEntity(long charId) => Manager() is { } m ? Live(Invoke1(_getCharEntity!, m, charId)) : null;

    public void CharIds(List<long> into)
    {
        into.Clear();
        if (Manager() is not { } m) return;
        var list = _charIds!.GetValue(m);
        var n = StellarInterop.Count(list);
        for (var i = 0; i < n; i++)
            if (StellarInterop.Item(list, i) is { } v) into.Add(Convert.ToInt64(v));
    }

    /// <summary>Every entity uuid in <c>ZEntityMgr.EntityDict</c> (players, NPCs, pets, mounts, monsters, scene objects…).
    /// Once per freeze press, never per frame (the key enumeration allocates).</summary>
    public void EntityUuids(List<long> into)
    {
        into.Clear();
        if (Manager() is not { } m) return;
        try
        {
            var dict = _entityDict!.GetValue(m);
            var keys = dict?.GetType().GetProperty("Keys")?.GetValue(dict);
            var en = keys?.GetType().GetMethod("GetEnumerator", Type.EmptyTypes)?.Invoke(keys, null);
            if (en is null) return;
            var move = en.GetType().GetMethod("MoveNext")!;
            var current = en.GetType().GetProperty("Current")!;
            while (move.Invoke(en, null) is true) into.Add(Convert.ToInt64(current.GetValue(en)));
        }
        catch { into.Clear(); }   // the dictionary changed under us: the caller sees no entities this time
    }

    /// <summary>The game's <c>EEntityType</c> of <paramref name="entity"/> (<c>ZEntity.LuaEntType</c>; see
    /// <c>FreezeKinds</c>), or −1 when it cannot be read.</summary>
    public int EntType(object entity)
    {
        try { return Convert.ToInt32(_entType!.GetValue(entity)); }
        catch { return -1; }
    }

    public long Uuid(object entity) => Convert.ToInt64(_entUuid!.GetValue(entity));

    public object? LiveModel(object? entity)
    {
        if (entity is null || !Resolve()) return null;
        var model = _model!.GetValue(entity);
        return model is null || _modelDestroying!.GetValue(model) is true ? null : model;
    }

    public Vector3? AttrPosition(object model)
    {
        _arg1[0] = model;
        try { return _attrPos!.Invoke(null, _arg1) is Vector3 v ? v : null; }
        catch { return null; }
    }

    public Vector3? Chest(object model)
    {
        try { return _chest!.Invoke(model, null) is Vector3 v ? v : null; }
        catch { return null; }
    }

    /// <summary>The <c>IsDestroying</c> liveness check alone, for a caller that already holds the entity object
    /// (e.g. a hook's own postfix argument) and wants to skip the <c>GetEntity(uuid)</c> re-lookup — which is not
    /// proven to find the entity at the exact same instant it was just added (Task 9 round 2). Public so
    /// <c>GameFreezeBackend</c> can apply it directly.</summary>
    public object? Live(object? entity) => entity is not null && Resolve() && _entDestroying!.GetValue(entity) is not true ? entity : null;

    private object? Invoke1(MethodInfo method, object target, long arg)
    {
        _arg1[0] = arg;
        try { return method.Invoke(target, _arg1); }
        catch { return null; }
    }

    private bool Resolve()
    {
        if (_attrPos is not null) return true;
        var mgr = _types.FindType(ManagerType);
        var ent = _types.FindType(EntityType);
        var model = _types.FindType(ModelType);
        var ext = _types.FindType(AttrExtType);
        if (mgr is null || ent is null || model is null || ext is null || !_manager.Resolve(mgr)) return false;
        _playerUuid = StellarInterop.FindPropertyUp(mgr, "PlayerUuid");
        _charIds = StellarInterop.FindPropertyUp(mgr, "CharIdList");
        _entityDict = StellarInterop.FindPropertyUp(mgr, "EntityDict");
        _entType = StellarInterop.FindPropertyUp(ent, "LuaEntType");
        _getEntity = mgr.GetMethod("GetEntity", AnyInstance, null, new[] { typeof(long) }, null);
        _getCharEntity = mgr.GetMethod("GetCharEntity", AnyInstance, null, new[] { typeof(long) }, null);
        _model = StellarInterop.FindPropertyUp(ent, "Model");
        _entDestroying = StellarInterop.FindPropertyUp(ent, "IsDestroying");
        _entUuid = StellarInterop.FindPropertyUp(ent, "Uuid");
        _modelDestroying = StellarInterop.FindPropertyUp(model, "IsDestroying");
        _chest = model.GetMethod("GetChestPosition", AnyInstance, null, Type.EmptyTypes, null);
        var attrPos = ext.GetMethod("GetAttrGoPosition", BindingFlags.Public | BindingFlags.Static, null, new[] { model }, null);
        if (_playerUuid is null || _charIds is null || _entityDict is null || _entType is null || _getEntity is null ||
            _getCharEntity is null || _model is null || _entDestroying is null || _entUuid is null || _modelDestroying is null ||
            _chest is null || attrPos is null) return false;
        _attrPos = attrPos;   // set last: it is the "fully resolved" sentinel
        return true;
    }
}
