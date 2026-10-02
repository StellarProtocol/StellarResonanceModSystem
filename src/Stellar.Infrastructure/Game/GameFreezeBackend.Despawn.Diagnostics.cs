using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The despawn sequence of a frozen monster (diagnostics only; owner report 2026-10-02: "when mobs is killed,
/// it's gone even in freeze mode"; owner choice: a monster killed while frozen should stay visible, as it was, until
/// unfreeze — NOT implemented here, this only captures the order of events to design it). Hooks, chained on the game's own
/// removal path (release_3.7 interop, ilspycmd): <c>ZEntityMgr.RemoveEntity(uuid, EDisappearType, removeImmediately)</c>
/// prefix + postfix, <c>removeEntity</c>, <c>removeEntityCtrl</c>, the <c>GetDelayRemoveTime</c> result (the death delay,
/// attributed to the RemoveEntity in flight), <c>destroyEntity</c>, and on the entity <c>ZEntity.Dead / OnDead /
/// Disappear</c> and <c>MonsterEnt.OnDisappear</c>. Logged only for this freeze's targets / watched monsters, ≤ 80 event
/// lines per freeze; each step also lands in the entity's <c>despawn=</c> sequence on its unfreeze summary line.</summary>
internal sealed partial class GameFreezeBackend
{
    private const int DespawnSeqCap = 12;
    private readonly Dictionary<long, List<string>> _diagDespawnSeq = new();
    private long _diagRemoving;
    private int _diagDespawns;

    private void InstallDespawnDiag(HarmonyGameMethodHooker hooker)
    {
        var mgr = _types.FindType(GameEntityAccess.ManagerType);
        var ent = _types.FindType(GameEntityAccess.EntityType);
        var monster = _types.FindType("Panda.ZGame.MonsterEnt");
        try
        {
            if (mgr is not null)
            {
                hooker.PrefixAllOverloads(mgr, "RemoveEntity", OnRemoveEntityDiag);
                hooker.PostfixAllOverloads(mgr, "RemoveEntity", OnRemovedEntityDiag);
                hooker.PostfixAllOverloads(mgr, "removeEntity", (_, a) => DespawnStep(EntityArg(a), "removeEntity", a));
                hooker.PostfixAllOverloads(mgr, "removeEntityCtrl", (_, a) => DespawnStep(EntityArg(a), "removeEntityCtrl", a));
                hooker.PostfixAllOverloads(mgr, "destroyEntity", (_, a) => DespawnStep(EntityArg(a), "destroyEntity", a));
                hooker.PostfixResultAllOverloads(mgr, "GetDelayRemoveTime", OnDelayRemoveTimeDiag);
            }
            if (ent is not null)
            {
                hooker.PostfixAllOverloads(ent, "Dead", (e, a) => DespawnStep(e, "Dead", a));
                hooker.PostfixAllOverloads(ent, "OnDead", (e, a) => DespawnStep(e, "OnDead", a));
                hooker.PostfixAllOverloads(ent, "Disappear", (e, a) => DespawnStep(e, "Disappear", a));
            }
            if (monster is not null) hooker.PostfixAllOverloads(monster, "OnDisappear", (e, a) => DespawnStep(e, "OnDisappear", a));
        }
        catch (Exception ex) { _log.Warning(DiagTag + "despawn hooks failed: " + ex.Message); }
    }

    private void ResetDespawnDiag()
    {
        _diagDespawnSeq.Clear();
        _diagRemoving = 0;
        _diagDespawns = 0;
    }

    private static object? EntityArg(object?[] args) => args.Length > 0 ? args[0] : null;

    // Prefix on RemoveEntity(long uuid, EDisappearType, bool): the entity is still in the dictionary here.
    private void OnRemoveEntityDiag(object? _, object?[] args)
    {
        if (!StellarDiagnostics.IsEnabled || _diagSink is null || !_diagSink.Enter() || args.Length == 0 || args[0] is not long uuid) return;
        if (!DiagCares(uuid)) return;
        _diagRemoving = uuid;
        _diagDespawns++;
        var held = _held.Contains(uuid);
        DespawnLog(uuid, $"RemoveEntity type={Arg(args, 1)} imm={Arg(args, 2)} st1={YN(_ledger.Factors.ContainsKey(uuid))} " +
                         $"st2={YN(_ledger.Speeds.ContainsKey(uuid))} held={YN(held)} k={_entities.Kind(uuid)}");
    }

    // Postfix on RemoveEntity: where the entity went — still served, queued for a delayed removal (a death), or gone.
    private void OnRemovedEntityDiag(object? _, object?[] args)
    {
        var uuid = _diagRemoving;
        _diagRemoving = 0;
        if (uuid == 0 || _diagSink is null || !_diagSink.Active) return;
        DespawnLog(uuid, $"removed inDict={YN(_entities.EntityByUuid(uuid) is not null)} delayed={YN(_diagColls!.DelayRemoved(uuid) is not null)}");
    }

    private void OnDelayRemoveTimeDiag(object? result)
    {
        if (_diagRemoving == 0 || _diagSink is null || !_diagSink.Active) return;
        DespawnLog(_diagRemoving, $"delay sec={result}");
    }

    /// <summary>A removal step on an entity object (or an entity argument): logged when it is one this freeze cares about.</summary>
    private void DespawnStep(object? entity, string phase, object?[] args)
    {
        if (!StellarDiagnostics.IsEnabled || _diagSink is null || !_diagSink.Enter() || entity is null) return;
        var p = FreezeDiagReader.Ptr(entity);
        long uuid;
        if (_diagCounters!.TryOwner(p, out var mapped) && mapped != 0) uuid = mapped;
        else if (_diagRemoving != 0) uuid = _diagRemoving;   // inside the RemoveEntity in flight: same entity
        else return;   // not a watched monster: no reflected read for the rest (bullets churn here)
        DespawnLog(uuid, phase + (args.Length > 1 ? $" type={Arg(args, 1)}" : ""));
    }

    private bool DiagCares(long uuid) => _diagTargets.Contains(uuid) || _diagAppeared.Contains(uuid) || _diagCounters!.IsWatched(uuid);

    private void DespawnLog(long uuid, string what)
    {
        var now = Environment.TickCount64;
        var t = _diagClock!.Elapsed(now);
        if (!_diagDespawnSeq.TryGetValue(uuid, out var seq)) _diagDespawnSeq[uuid] = seq = new List<string>();
        if (seq.Count < DespawnSeqCap) seq.Add($"{what.Split(' ')[0]}@{t}");
        if (_diagClock.TakeEvent(now)) _log.Info($"{DiagTag}despawn u={uuid} t={t} f={UnityEngine.Time.frameCount} {what}");
    }

    private string DespawnText(long uuid) => _diagDespawnSeq.TryGetValue(uuid, out var seq) ? string.Join(">", seq) : "-";

    private static string Arg(object?[] args, int i) => i < args.Length ? args[i]?.ToString() ?? "null" : "?";
}
