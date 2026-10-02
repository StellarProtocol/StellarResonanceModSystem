using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Action, moment, expression, look and facing on ONE model (recon § 1 table): the local player through the
/// no-model overloads and its live entity, a copy / generated model through the <c>ZModel</c> overloads. The game's own
/// emote check gates every action. A gone model makes every call a no-op. Main thread.</summary>
internal sealed class ModelPoser
{
    private readonly PoseCalls _c;
    private readonly PoseSubject _s;
    private int _actionId;
    private bool _adopted;   // _actionId is the model's own running action (held, never played by us)
    private float? _priorSpeed;

    public ModelPoser(PoseCalls calls, PoseSubject subject)
    {
        _c = calls;
        _s = subject;
        Look = new LookDriver(calls.Models, calls.MainCamera, subject.Model);
    }

    public LookDriver Look { get; }
    public object Model => _s.Model;
    public bool Live => _c.Models.IsLive(_s.Model);

    public bool PlayAction(int actionId)
    {
        if (!Live || !_c.Lua.Allowed(actionId)) return false;
        _actionId = actionId;
        _adopted = false;
        return _s.Self ? _c.Actions.PlaySelf(actionId) : _c.Actions.PlayModel(_s.Model, actionId);
    }

    /// <summary>Holds (0–1) or releases (−1) the action. Nothing played by us yet: holding takes over the action the model
    /// is already doing (the person's own emote, or the one a photo copy inherited) — it is never re-played.</summary>
    public void SetMoment(float fraction)
    {
        if (!Live) return;
        if (_actionId == 0 && fraction >= 0f && _c.Models.ReadAction(_s.Model) is { IsPlaying: true } running)
        {
            _actionId = running.ActionId;
            _adopted = true;
        }
        if (_actionId == 0) return;
        var time = PoseMath.PersistTime(fraction, Total());
        if (_s.Self) _c.Actions.PersistSelf(time);
        else _c.Actions.PersistModel(_s.Model, time);
    }

    /// <summary>What the model is doing now, whoever started it (compiled reads, liveness-gated).</summary>
    public PoseActionReading ReadAction() => _c.Models.ReadAction(_s.Model);

    /// <summary>Held = <c>SetLuaAttrEmoteInfo(face, -1, true)</c> (run 5 (1): no expiry, no timer); not held = the game's
    /// own 5 s <c>PlayEmote</c>; null clears (<c>SetLuaAttrEmoteInfo(0)</c> + <c>ResetEmote</c>).</summary>
    public void SetExpression(ExpressionInfo? expression, bool hold)
    {
        if (!Live) return;
        if (expression is null) { ClearFace(); return; }
        var face = PoseMath.FaceId(expression, _s.Gender);
        if (hold) _c.Models.HoldFace(_s.Model, face);
        else if (_s.Self) _c.Actions.FaceSelf(face);
        else _c.Actions.FaceModel(_s.Model, face);
    }

    public void ClearFace()
    {
        if (!Live) return;
        _c.Models.ClearFace(_s.Model);
        if (_s.Self) _c.Actions.ResetFaceSelf();
        else _c.Actions.ResetFaceModel(_s.Model);
    }

    public void SetLook(LookPart part, LookMode mode, bool locked)
    {
        if (Live) Look.Apply(part, mode, locked);
    }

    public void Aim(LookPart part, float x, float y)
    {
        if (Live) Look.Aim(part, x, y);
    }

    /// <summary>Self: <c>SetEntityRotation</c> on the live entity (rides the next movement sync, as the panel does);
    /// others: <c>SetAttrGoRotation</c> on the model.</summary>
    public void SetYaw(float offset)
    {
        if (!Live) return;
        var yaw = _s.BaseYaw + offset;
        if (!_s.Self) _c.Models.SetYaw(_s.Model, yaw);
        else if (_s.Entity() is { } entity) _c.Spawn.RotateEntity(entity, yaw);
    }

    /// <summary>Persist −1 + <c>ResetAction</c> (self: one NewMove, the game's own photo behaviour). Skipped when nothing
    /// was playing. An adopted action (the person's own, only held) is just released (persist −1): it was theirs, so it
    /// carries on rather than being stopped.</summary>
    public void ResetAction()
    {
        if (!Live || _actionId == 0) return;
        if (_s.Self) { _c.Actions.PersistSelf(-1f); if (!_adopted) _c.Actions.ResetSelf(); }
        else { _c.Actions.PersistModel(_s.Model, -1f); if (!_adopted) _c.Actions.ResetModel(_s.Model); }
        _actionId = 0;
        _adopted = false;
    }

    /// <summary>Where a copy / generated model stands (the orbit centre, every frame); null for the live player.</summary>
    public Position3D? Position => _s.Self ? null : _c.Models.FastPosition(_s.Model);

    /// <summary>Freeze stage 2 on the model: a drawn speed above 0 becomes 0, the prior kept; false restores it. Self: a
    /// no-op — the scene freeze already freezes the live player as an entity.</summary>
    public void SetFrozen(bool frozen)
    {
        if (_s.Self || !Live) return;
        if (!frozen)
        {
            if (_priorSpeed is not float prior) return;
            _priorSpeed = null;
            _c.Models.SetDrawnSpeed(_s.Model, prior);
            return;
        }
        if (_priorSpeed is not null || _c.Models.DrawnSpeed(_s.Model) is not float speed || speed <= FreezeLedger.SpeedEpsilon) return;
        _priorSpeed = speed;
        _c.Models.SetDrawnSpeed(_s.Model, 0f);
    }

    private float Total() => PoseMath.Total(_c.Models.TotalTime(_s.Model), _c.Actions.AnimTotal(_actionId, _s.Gender));
}
