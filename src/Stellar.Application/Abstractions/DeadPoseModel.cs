using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>The model a backend returns when it made nothing for a person (the open already reported failure and nothing
/// is hidden or loading — e.g. inside the scene-change settle window). <see cref="Stellar.Application.Services.PoseTarget"/>
/// treats it as "no model", so a refusal never holds a photo-member slot (unlike a half-made copy). Stateless; one shared
/// instance.</summary>
internal sealed class DeadPoseModel : IPoseModel
{
    /// <summary>The shared instance.</summary>
    public static readonly DeadPoseModel Instance = new();

    private DeadPoseModel() { }

    public bool PlayAction(int actionId) => false;
    public void SetMoment(float fraction, bool adopt) { }
    public PoseActionReading ReadAction() => PoseActionReading.None;
    public void SetExpression(ExpressionInfo? expression, bool hold) { }
    public void SetLook(LookPart part, LookMode mode, bool locked) { }
    public void Aim(LookPart part, float x, float y) { }
    public void SetYaw(float offsetDegrees) { }
    public void Close(PoseTouches touched) { }
    public Position3D? Position => null;
    public void SetFrozen(bool frozen) { }
}
