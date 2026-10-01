using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The model returned when a person could not be prepared (the open already reported failure).</summary>
internal sealed class DeadPoseModel : IPoseModel
{
    public bool PlayAction(int actionId) => false;
    public void SetMoment(float fraction) { }
    public float ReadMoment() => -1f;
    public void SetExpression(ExpressionInfo? expression, bool hold) { }
    public void SetLook(LookPart part, LookMode mode, bool locked) { }
    public void Aim(LookPart part, float x, float y) { }
    public void SetYaw(float offsetDegrees) { }
    public void Close(PoseTouches touched) { }
    public Position3D? Position => null;
    public void SetFrozen(bool frozen) { }
}
