using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// An NPC, posed as a generated stand-in (recon run 5 (2), the game's Partner path): the model is requested from
/// <c>NpcTable.ModelID</c>; <c>preCreate</c> places it where the real NPC stands, turns every show layer on and copies the
/// scene NPC's base idle (again on load); on load the real NPC is hidden and the model becomes posable. The real NPC is
/// never posed. The load / error callbacks run their handling on the main thread (<see cref="PoseCalls.OnMain"/>, decision
/// Q11). Close removes the model (<c>RecycleModelByLua</c>) and shows the real NPC; a close before the load removes the
/// model the moment it arrives, never showing it (<see cref="NpcLoadState"/>: the callbacks' keep-alive and the model
/// reference live exactly as long as they are needed). An error after <c>preCreate</c> already handed over a model also
/// recycles it — the generate path does not own a half-made model on failure, so a dropped reference there would leave a
/// duplicate NPC visible. Main thread.
/// </summary>
internal sealed class NpcPoseModel : IPoseModel, IVisibleModel
{
    private readonly PoseCalls _c;
    private readonly NpcSource _src;
    private readonly Action<bool> _loaded;
    private readonly NpcLoadState _state = new();
    private ModelPoser? _p;
    private bool _hidden;

    public NpcPoseModel(PoseCalls calls, NpcSource source, Action<bool> loaded)
    {
        _c = calls;
        _src = source;
        _loaded = loaded;
    }

    /// <summary>Requests the model; false when the request could not be made (no callback will come).</summary>
    public bool Start(int modelId)
    {
        if (!_c.Spawn.Generate(modelId, OnPre, OnLoad, OnError, out var keepAlive)) return false;
        _state.Requested(keepAlive);
        return true;
    }

    public bool PlayAction(int actionId) => _p is { } p && p.PlayAction(actionId);
    public void SetMoment(float fraction, bool adopt) => _p?.SetMoment(fraction, adopt);
    public PoseActionReading ReadAction() => _p?.ReadAction() ?? PoseActionReading.None;
    public void SetExpression(ExpressionInfo? expression, bool hold) => _p?.SetExpression(expression, hold);
    public void SetLook(LookPart part, LookMode mode, bool locked) => _p?.SetLook(part, mode, locked);
    public void Aim(LookPart part, float x, float y) => _p?.Aim(part, x, y);
    public void SetYaw(float offsetDegrees) => _p?.SetYaw(offsetDegrees);
    public Position3D? Position => _p?.Position;
    public object? VisibleModel => _p is { Live: true } p ? p.Model : null;
    public void SetFrozen(bool frozen) => _p?.SetFrozen(frozen);

    /// <summary>Idempotent; safe while loading (also releases the pending load's callbacks).</summary>
    public void Close(PoseTouches touched)
    {
        _p = null;
        if (_state.Close() is { } model) Remove(model);
    }

    // The game's pre-create runs inside its own model build (probe run 5: main thread); it only remembers and prepares.
    private void OnPre(object model)
    {
        _state.Created(model);
        try
        {
            _c.Models.Place(model, _src.Position, _src.Rotation);
            _c.Models.ShowAll(model);
            CopyBaseIdle(model);
        }
        catch (Exception ex) { _c.Warn("posing: preparing the NPC model failed: " + (ex.InnerException ?? ex).Message); }
    }

    // Q11 guard: the game's async continuation is expected on the main thread (probe run 5); if it ever is not, the
    // handling is posted there instead of touching Unity off-thread.
    private void OnLoad(object model) => _c.OnMain(() => HandleLoad(model));

    private void OnError(object error) => _c.OnMain(() => HandleError(error));

    private void HandleLoad(object model)
    {
        var step = _state.Loaded(model, out var target);
        if (step == NpcLoadStep.Fail) { _c.Warn("posing: the NPC model loaded with nothing to pose."); _loaded(false); return; }
        if (target is null) return;
        if (step == NpcLoadStep.Recycle) Remove(target);   // closed while loading: removed now, never shown
        else if (step == NpcLoadStep.Ready) Ready(target);
    }

    private void Ready(object model)
    {
        try { CopyBaseIdle(model); }
        catch (Exception ex) { _c.Warn("posing: copying the NPC's idle failed: " + (ex.InnerException ?? ex).Message); }
        try { _hidden = _src.Entity() is { } e && _c.Spawn.SetVisible(e, false); }
        catch (Exception ex) { _c.Warn("posing: hiding the real NPC failed: " + (ex.InnerException ?? ex).Message); }
        _p = new ModelPoser(_c, new PoseSubject(_src.Entity, model, false, _src.Gender, _src.Rotation.eulerAngles.y));
        _loaded(true);
    }

    private void HandleError(object error)
    {
        var step = _state.Failed(out var abandoned);
        if (abandoned is { } m) Remove(m);   // a pre-created model the generate path does not own: recycle it ourselves
        if (step != NpcLoadStep.Fail) return;
        _c.Warn("posing: the NPC model did not load: " + error);
        _loaded(false);
    }

    private void CopyBaseIdle(object model)
    {
        if (_c.Models.IsLive(_src.SceneModel)) _c.Spawn.ApplyBaseIdle(_src.SceneModel, model);
    }

    // The model was already forgotten by _state (and _p cleared): nothing reads it after the recycle.
    private void Remove(object model)
    {
        try { if (_c.Models.IsLive(model)) _c.Spawn.RecycleModel(model); }
        catch (Exception ex) { _c.Warn("posing: removing the NPC model failed: " + (ex.InnerException ?? ex).Message); }
        if (!_hidden) return;
        _hidden = false;
        try { if (_src.Entity() is { } e) _c.Spawn.SetVisible(e, true); }
        catch (Exception ex) { _c.Warn("posing: showing the real NPC failed: " + (ex.InnerException ?? ex).Message); }
    }
}
