using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The local player, posed live (recon § 4 "Self"). Close undoes only what was touched, each step isolated so
/// one failing never skips the rest: action (persist −1 + ResetAction), expression (clear), head/eyes (the pre-pose
/// snapshot), facing (the yaw at first touch).</summary>
internal sealed class SelfPoseModel : IPoseModel
{
    private readonly ModelPoser _p;
    private readonly LookAtSnapshot? _pre;
    private readonly Action<string> _warn;
    private bool _closed;

    public SelfPoseModel(ModelPoser poser, LookAtSnapshot? pre, Action<string> warn)
    {
        _p = poser;
        _pre = pre;
        _warn = warn;
    }

    public bool PlayAction(int actionId) => !_closed && _p.PlayAction(actionId);
    public void SetMoment(float fraction, bool adopt) { if (!_closed) _p.SetMoment(fraction, adopt); }
    public PoseActionReading ReadAction() => _closed ? PoseActionReading.None : _p.ReadAction();
    public void SetExpression(ExpressionInfo? expression, bool hold) { if (!_closed) _p.SetExpression(expression, hold); }
    public void SetLook(LookPart part, LookMode mode, bool locked) { if (!_closed) _p.SetLook(part, mode, locked); }
    public void Aim(LookPart part, float x, float y) { if (!_closed) _p.Aim(part, x, y); }
    public void SetYaw(float offsetDegrees) { if (!_closed) _p.SetYaw(offsetDegrees); }
    public Position3D? Position => null;                 // you: the camera follows your entity
    public void SetFrozen(bool frozen) { }               // you: never frozen (scene-stays spec § 3) — moving cancels your held emote, as in the game

    public void Close(PoseTouches touched)
    {
        if (_closed) return;
        _closed = true;
        if ((touched & PoseTouches.Action) != 0) Step("pose", _p.ResetAction);
        if ((touched & PoseTouches.Expression) != 0) Step("expression", _p.ClearFace);
        if ((touched & (PoseTouches.Head | PoseTouches.Eyes)) != 0) Step("look", () => { if (_p.Live) _p.Look.Restore(_pre); });
        if ((touched & PoseTouches.Yaw) != 0) Step("facing", () => _p.SetYaw(0f));
    }

    private void Step(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { _warn($"posing: restoring your {what} failed: {(ex.InnerException ?? ex).Message}"); }
    }
}
