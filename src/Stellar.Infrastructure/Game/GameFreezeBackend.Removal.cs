using System;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>A monster killed while frozen stays visible as it was until the freeze ends (owner choice 2026-10-02; the rule
/// is <see cref="DeferredRemovals"/>). A run-original GATE on the one <c>ZEntityMgr.RemoveEntity</c> prefix trampoline
/// (<see cref="HarmonyGameMethodHooker.GatePrefixAllOverloads"/>): a deferred call skips the game's body AND the chained
/// prefix callbacks on that method (posing's despawn), so each of them runs exactly once — when the call is REPLAYED through
/// the game's own <c>RemoveEntity</c> with its original arguments. The entity stays in <c>EntityDict</c> meanwhile, so the
/// time pause keeps it still and the position hold keeps covering it. The replay runs, in arrival order: on unfreeze, BEFORE
/// anything else unfreezes (each replayed entity is released from the hold — snapped to its logical pose — and removed in the
/// same frame, so it never visibly revives); and from a PREFIX on <c>ZEntityMgr.ClearEntities</c>, the game's own scene
/// teardown (leave scene, disconnect, return to login: <c>OnLeaveScene</c> → <c>ClearEntities</c> → <c>doClearEntities</c>),
/// before the game destroys the scene's entities. Framework releases (zone change, cutscene, disconnect, plugin unload) all
/// end in <c>UnfreezeAll</c>. Event-driven; the gate returns after two field reads unless frozen and the call is a
/// <c>Dead</c> removal.</summary>
internal sealed partial class GameFreezeBackend
{
    private readonly DeferredRemovals _removals = new();
    private readonly Func<long, nint> _identityNow;
    private MethodInfo? _removeEntity;

    /// <summary>Kill switch (read once, at install on the first freeze): <c>0</c> = killed monsters vanish as before.</summary>
    internal const string DeferEnvVar = "STELLAR_FREEZE_DEFER_REMOVALS";

    private void InstallRemovalGate(HarmonyGameMethodHooker hooker)
    {
        if (Environment.GetEnvironmentVariable(DeferEnvVar) == "0") { _log.Info($"{Tag}deferred removals OFF ({DeferEnvVar}=0)"); return; }
        if (_types.FindType(GameEntityAccess.ManagerType) is not { } mgr) { WarnOnce("deferhook", "a monster killed while frozen will vanish (ZEntityMgr not found)"); return; }
        _removeEntity = mgr.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "RemoveEntity" && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType == typeof(long));
        if (_removeEntity is null) { WarnOnce("deferhook", "a monster killed while frozen will vanish (RemoveEntity not found)"); return; }
        try
        {
            hooker.GatePrefixAllOverloads(mgr, "RemoveEntity", OnRemoveEntityGate);
            hooker.PrefixAllOverloads(mgr, "ClearEntities", OnClearEntities);
        }
        catch (Exception ex) { WarnOnce("deferhook", "deferred-removal hook failed: " + ex.Message); }
    }

    // Gate on RemoveEntity(long uuid, EDisappearType, bool removeImmediately). True = the game removes it now. Off the main
    // thread (the thread FreezeAll ran on) every call passes (DeferredRemovals.Decide), with no entity read.
    private bool OnRemoveEntityGate(object? _, object?[] args)
    {
        if (!_removals.Armed || _removals.Replaying || args.Length < 3 || args[0] is not long uuid) return true;
        var type = args[1]?.ToString();
        if (type != DeferredRemovals.DeadType && !_removals.IsQueued(uuid)) return true;   // no kind read for the rest
        var thread = Environment.CurrentManagedThreadId;
        var off = thread == 0 || thread != _mainThread;
        var entity = off ? null : _entities.EntityByUuid(uuid);
        var call = new DeferredRemovals.Call(uuid, type, args[2] is true, entity is null ? -1 : _entities.EntType(entity),
            _ledger.Excludes(uuid), Identity(entity), off);
        return _removals.Decide(call, args) != DeferredRemovals.Decision.Defer;
    }

    /// <summary>The entity's native pointer — its identity across a uuid reuse (0 = none).</summary>
    private static nint Identity(object? entity) => (entity as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;

    /// <summary>What <c>GetEntity(uuid)</c> serves now, as an identity (the replay's same-entity check).</summary>
    private nint IdentityNow(long uuid) => Identity(_entities.EntityByUuid(uuid));

    // Prefix on ZEntityMgr.ClearEntities(bool): the scene's entities are about to be destroyed — replay first.
    private void OnClearEntities(object? _, object?[] __)
    {
        if (_removals.Count > 0) FlushDeferred("scene clear");
    }

    /// <summary>Re-issues every deferred removal through the game's own <c>RemoveEntity</c>, in order, each entity released
    /// from the hold (snapped to its logical pose) just before its own removal. One failure never skips the rest.</summary>
    private void FlushDeferred(string why)
    {
        if (_removals.Count == 0) return;
        if (_entities.Manager() is not { } mgr || _removeEntity is null) { _removals.Drop(); return; }
        var n = _removals.Replay(_identityNow, (uuid, args) =>
        {
            try
            {
                Unhold(uuid);
                _removeEntity.Invoke(mgr, args);
            }
            catch (Exception ex) { WarnOnce("deferreplay", "could not replay a deferred removal: " + (ex.InnerException ?? ex).Message); }
        });
        OnDeferredFlushed(why, n);
    }

    partial void OnDeferredFlushed(string why, int replayed);
}
