using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Game side of posing (recon docs/recon/photo-posing-recon.md § 4 + run 5): the local player is posed live; another
/// player as the game's photo copy (<c>CloneModelForPhoto</c>, real player hidden); an NPC as a generated stand-in (the
/// Partner path; never the live NPC — its facing and idle cannot be restored). Gender for the face ids comes from the
/// person's own data (charBase for you, AttrGender for others). A <c>ZEntityMgr.RemoveEntity</c> prefix, installed on the
/// first open, reports despawns; the photo copy goes through <see cref="PhotoCopyMaker"/> (the game's Male-idle copy crash
/// guard + orphan clean-up, regression clone-nre-male-null-ridetpl). Inside the scene-change settle window (<see cref="SceneChanged"/>) nothing live is read:
/// no kind, no people, no open, and every handed-out model is a <see cref="SettledPoseModel"/> whose per-frame position
/// reads nothing until it settles. People listing lives in GamePosingBackend.People.cs. Main thread.
/// </summary>
internal sealed partial class GamePosingBackend : IPosingBackend
{
    private readonly PoseCalls _calls;
    private readonly GameEntityAccess _entities;
    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private readonly SceneSettleWindow _settle = new();
    private readonly PhotoCopyMaker _copies;
    private HarmonyGameMethodHooker? _hooker;
    private bool _hookTried;

    public GamePosingBackend(PoseCalls calls, GameEntityAccess entities, IGameTypeRegistry types, IPluginLog log)
    {
        _calls = calls;
        _entities = entities;
        _types = types;
        _log = log;
        _copies = new PhotoCopyMaker(calls.Actions, new RideTemplateCalls(types), log);
    }

    public event Action<long>? PersonRemoved;

    /// <summary>Called once the hot-update assemblies are ready; the despawn prefix installs on the first open.</summary>
    public void ArmHooks(HarmonyGameMethodHooker hooker) => _hooker = hooker;

    /// <summary>A scene leave (the <c>Game.OnLeaveScene</c> prefix) or enter (<c>IClientState.SceneChanged</c>): starts
    /// the settle window. Subscribe AFTER the free-camera release on the same events, so the release (which closes
    /// every model) runs first — it is never gated anyway (<see cref="SettledPoseModel.Close"/>).</summary>
    public void SceneChanged() => _settle.Arm();

    public PersonKind? KindOf(long uuid)
    {
        if (_settle.Settling || _entities.EntityByUuid(uuid) is not { } entity) return null;
        if (uuid == _entities.PlayerUuid()) return PersonKind.Self;
        return _entities.EntType(entity) switch
        {
            FreezeKinds.Char => PersonKind.Player,
            FreezeKinds.Npc => PersonKind.Npc,
            _ => null,
        };
    }

    /// <summary>The live person's own model (never a copy), compiled reads only; nothing inside the settle window.</summary>
    public PoseActionReading ReadAction(long uuid)
    {
        if (_settle.Settling) return PoseActionReading.None;
        var model = _entities.FastLiveModel(uuid);
        OnActionRead(uuid, model);
        return _calls.Models.ReadAction(model);
    }

    public IReadOnlyList<ExpressionInfo> ReadExpressions() => _calls.Lua.Expressions();

    public int MemberLimit() => _calls.Lua.MemberLimit();

    /// <summary>Never throws: a failure is <c>loaded(false)</c> + <see cref="DeadPoseModel.Instance"/> (holds no member slot), with whatever the failed open had made
    /// (copy, hidden player, requested model) already undone. <c>loaded</c> runs exactly once (<see cref="LoadedOnce"/>).</summary>
    public IPoseModel Open(long uuid, PersonKind kind, Action<bool> loaded)
    {
        var once = new LoadedOnce(loaded);
        if (_settle.Settling) return Fail(once, "the world is still loading — try again in a moment");
        EnsureHooks();
        var started = Stopwatch.GetTimestamp();
        IPoseModel? model;
        string? why;
        try
        {
            model = kind switch
            {
                PersonKind.Self => OpenSelf(out why),
                PersonKind.Player => OpenCopy(uuid, out why),
                _ => OpenNpc(uuid, ok => { OnNpcLoaded(uuid, ok, started); once.Report(ok); }, out why),
            };
        }
        catch (Exception ex)
        {
            model = null;
            why = $"could not prepare {kind} {uuid}: {(ex.InnerException ?? ex).Message}";
        }
        if (model is null) return Fail(once, why ?? $"could not prepare {kind} {uuid}");
        OnOpened(kind, uuid, started);
        if (kind != PersonKind.Npc) once.Report(true);   // NPCs report from their load / error callback
        return new SettledPoseModel(model, _settle);
    }

    private IPoseModel? OpenSelf(out string? why)
    {
        why = "you are not in the world";
        var entity = _entities.LocalEntity();
        if (entity is null || _entities.LiveModel(entity) is not { } model) return null;
        var gender = _calls.Lua.Gender(_entities.Uuid(entity), self: true);
        var poser = new ModelPoser(_calls, new PoseSubject(_entities.LocalEntity, model, true, gender, _calls.Models.Yaw(model)));
        return new SelfPoseModel(poser, _calls.Looks.Read(model), _calls.Warn);
    }

    // The copy is made, read and wrapped first; hiding the real player is the last step that can throw, and a throw at
    // any point after the copy exists shows the player again and removes the copy (the Open contract: on failure nothing
    // is hidden).
    private IPoseModel? OpenCopy(long uuid, out string? why)
    {
        why = "the player left";
        if (_entities.EntityByUuid(uuid) is not { } entity) return null;
        var gender = _calls.Lua.Gender(uuid, self: false);
        why = "the game made no photo copy";
        if (_copies.Make(entity, _entities.LiveModel(entity)) is not { } copy) return null;
        var hidden = false;
        try
        {
            Func<object?> source = () => _entities.EntityByUuid(uuid);
            var poser = new ModelPoser(_calls, new PoseSubject(source, copy, false, gender, _calls.Models.Yaw(copy)));
            hidden = _calls.Spawn.SetVisible(entity, false);
            return new ClonePoseModel(_calls, poser, source, hidden);
        }
        catch
        {
            Undo(() => { if (hidden) _calls.Spawn.SetVisible(entity, true); }, "showing the player again");
            Undo(() => _calls.Actions.Recycle(copy), "removing the copy");
            throw;
        }
    }

    private IPoseModel? OpenNpc(long uuid, Action<bool> loaded, out string? why)
    {
        why = "the NPC left";
        var entity = _entities.EntityByUuid(uuid);
        if (entity is null || _entities.LiveModel(entity) is not { } scene) return null;
        var modelId = _calls.Lua.NpcModelId(uuid);
        why = $"NPC {uuid} has no model id";
        if (modelId <= 0) return null;
        why = $"NPC {uuid}: its place could not be read";
        if (_calls.Models.Position(scene) is not { } pos || _calls.Models.Rotation(scene) is not { } rot) return null;
        var source = new NpcSource(() => _entities.EntityByUuid(uuid), scene, pos, rot, _calls.Lua.Gender(uuid, self: false));
        var model = new NpcPoseModel(_calls, source, loaded);
        why = $"NPC {uuid}: the model {modelId} could not be requested";
        try { return model.Start(modelId) ? model : null; }
        catch
        {
            Undo(() => model.Close(PoseTouches.None), "cancelling the NPC model");   // a late load is removed on arrival
            throw;
        }
    }

    private IPoseModel Fail(LoadedOnce loaded, string message)
    {
        _log.Warning("[Posing] " + message);
        loaded.Report(false);
        return DeadPoseModel.Instance;
    }

    private void Undo(Action step, string what)
    {
        try { step(); }
        catch (Exception ex) { _log.Warning($"[Posing] {what} failed: {(ex.InnerException ?? ex).Message}"); }
    }

    private void EnsureHooks()
    {
        if (_hookTried || _hooker is null) return;
        _hookTried = true;
        _copies.Install(_hooker, _types);
        if (_types.FindType(GameEntityAccess.ManagerType) is not { } mgr)
        {
            _log.Warning("[Posing] a person who leaves keeps their copy until the free camera ends (ZEntityMgr not found)");
            return;
        }
        try { _hooker.PrefixAllOverloads(mgr, "RemoveEntity", OnRemoveEntity); }
        catch (Exception ex) { _log.Warning("[Posing] despawn hook failed: " + ex.Message); }
    }

    // Prefix on ZEntityMgr.RemoveEntity(long uuid, EDisappearType, bool) — its only overload in release_3.7 (ilspycmd):
    // args[0] is the uuid; the entity is still alive. PosingService closes only that uuid's person (if selected).
    private void OnRemoveEntity(object? _, object?[] args)
    {
        OnDespawnPrefixFired();
        if (PersonRemoved is not { } removed || args.Length == 0 || args[0] is not long uuid) return;
        removed(uuid);
    }

    partial void OnOpened(PersonKind kind, long uuid, long started);
    partial void OnNpcLoaded(long uuid, bool ok, long started);
    partial void OnDespawnPrefixFired();
    partial void OnActionRead(long uuid, object? model);
}
