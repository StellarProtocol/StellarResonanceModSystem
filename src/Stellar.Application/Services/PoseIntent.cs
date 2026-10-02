using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>What a plugin asked for on one person — replayed in one go when an NPC model finishes loading, and the
/// record of what a reset must undo (<see cref="Touched"/>). Main thread.</summary>
internal sealed class PoseIntent
{
    private float _headX, _headY, _eyesX, _eyesY;

    public int ActionId { get; private set; }

    /// <summary>The action is the one the person was already doing (adopted to hold it), not one we played: a replay
    /// never plays it again (that would restart it).</summary>
    public bool Adopted { get; private set; }
    public float Moment { get; private set; } = -1f;
    public ExpressionInfo? Expression { get; private set; }
    public bool Hold { get; private set; }
    public LookMode Head { get; private set; }
    public LookMode Eyes { get; private set; }
    public bool HeadLocked { get; private set; }
    public bool EyesLocked { get; private set; }
    public float Yaw { get; private set; }
    public PoseTouches Touched { get; private set; }

    /// <summary>An action is held at a point (not playing).</summary>
    public bool Paused => ActionId != 0 && Moment >= 0f;

    public void SetAction(int actionId)
    {
        ActionId = actionId;
        Adopted = false;
        Moment = -1f;
        Touched |= PoseTouches.Action;
    }

    /// <summary>Forgets an adopted action the model turned out not to have (a copy that came up idle, an NPC stand-in):
    /// nothing is held, and a reset has no action to undo.</summary>
    public void DropAdopted()
    {
        if (!Adopted) return;
        ActionId = 0;
        Adopted = false;
        Moment = -1f;
        Touched &= ~PoseTouches.Action;
    }

    /// <summary>Takes over the action the person is already doing, so it can be held (never re-played).</summary>
    public void AdoptAction(int actionId)
    {
        SetAction(actionId);
        Adopted = true;
    }

    public void SetMoment(float moment) => Moment = moment;

    public void SetExpression(ExpressionInfo? expression, bool hold)
    {
        Expression = expression;
        Hold = hold;
        Touched |= PoseTouches.Expression;
    }

    public void SetLook(LookPart part, LookMode mode, bool locked)
    {
        if (part == LookPart.Head) { Head = mode; HeadLocked = locked; Touched |= PoseTouches.Head; }
        else { Eyes = mode; EyesLocked = locked; Touched |= PoseTouches.Eyes; }
    }

    public void SetAim(LookPart part, float x, float y)
    {
        if (part == LookPart.Head) { _headX = x; _headY = y; }
        else { _eyesX = x; _eyesY = y; }
    }

    public void SetYaw(float yaw)
    {
        Yaw = yaw;
        Touched |= PoseTouches.Yaw;
    }

    public LookMode ModeOf(LookPart part) => part == LookPart.Head ? Head : Eyes;
    public float AimX(LookPart part) => part == LookPart.Head ? _headX : _eyesX;
    public float AimY(LookPart part) => part == LookPart.Head ? _headY : _eyesY;

    public void Clear()
    {
        ActionId = 0;
        Adopted = false;
        Moment = -1f;
        Expression = null;
        Hold = false;
        Head = Eyes = LookMode.Default;
        HeadLocked = EyesLocked = false;
        _headX = _headY = _eyesX = _eyesY = 0f;
        Yaw = 0f;
        Touched = PoseTouches.None;
    }

    /// <summary>Applies everything asked for so far: the action first, the pause last (a look change on a paused person
    /// re-applies the pause — recon § 1, Head Free). An adopted action is never played: it is held only when the opened
    /// model really runs an action, else it is dropped (<see cref="DropAdopted"/>) so nothing claims a hold the model
    /// lacks. Returns the action's result (true without an action).</summary>
    public bool ReplayInto(IPoseModel m)
    {
        if (Adopted && !m.ReadAction().IsPlaying) DropAdopted();
        var played = ActionId == 0 || Adopted || m.PlayAction(ActionId);
        if ((Touched & PoseTouches.Expression) != 0) m.SetExpression(Expression, Hold);
        if ((Touched & PoseTouches.Head) != 0) ReplayLook(m, LookPart.Head, Head, HeadLocked);
        if ((Touched & PoseTouches.Eyes) != 0) ReplayLook(m, LookPart.Eyes, Eyes, EyesLocked);
        if ((Touched & PoseTouches.Yaw) != 0) m.SetYaw(Yaw);
        if (Paused) m.SetMoment(Moment, Adopted);
        return played;
    }

    private void ReplayLook(IPoseModel m, LookPart part, LookMode mode, bool locked)
    {
        m.SetLook(part, mode, locked);
        if (mode == LookMode.Free) m.Aim(part, AimX(part), AimY(part));
    }
}
