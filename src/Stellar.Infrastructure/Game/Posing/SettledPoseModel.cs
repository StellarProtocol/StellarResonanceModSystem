using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Every model the backend hands out, behind the scene-change settle window (Task 6 carry-over): while
/// <see cref="SceneSettleWindow.Settling"/> the per-frame <see cref="Position"/> (the orbit centre,
/// <c>IPosing.TryGetVisiblePosition</c>) reads nothing and every control is a no-op, but the release side — unfreeze and
/// <see cref="Close"/> — always reaches the model, so a release on scene change is never skipped. After the close the
/// model is forgotten: nothing reads a recycled copy or stand-in again. Pure (unit-tested). Main thread.
/// </summary>
internal sealed class SettledPoseModel : IPoseModel
{
    private readonly SceneSettleWindow _settle;
    private IPoseModel? _inner;

    public SettledPoseModel(IPoseModel inner, SceneSettleWindow settle)
    {
        _inner = inner;
        _settle = settle;
    }

    private IPoseModel? Usable => _inner is { } m && !_settle.Settling ? m : null;

    public Position3D? Position => Usable?.Position;
    public bool PlayAction(int actionId) => Usable is { } m && m.PlayAction(actionId);
    public void SetMoment(float fraction) => Usable?.SetMoment(fraction);
    public PoseActionReading ReadAction() => Usable?.ReadAction() ?? PoseActionReading.None;
    public void SetExpression(ExpressionInfo? expression, bool hold) => Usable?.SetExpression(expression, hold);
    public void SetLook(LookPart part, LookMode mode, bool locked) => Usable?.SetLook(part, mode, locked);
    public void Aim(LookPart part, float x, float y) => Usable?.Aim(part, x, y);
    public void SetYaw(float offsetDegrees) => Usable?.SetYaw(offsetDegrees);

    /// <summary>Freezing waits out the window; unfreezing (a restore) always goes through.</summary>
    public void SetFrozen(bool frozen)
    {
        if (frozen) Usable?.SetFrozen(true);
        else _inner?.SetFrozen(false);
    }

    public void Close(PoseTouches touched)
    {
        if (_inner is not { } m) return;
        _inner = null;
        m.Close(touched);
    }
}
