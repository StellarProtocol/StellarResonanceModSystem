using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Another player, posed as the game's photo copy (recon run 4c/4d/5b): <c>CloneModelForPhoto</c> with the real
/// player hidden. Every control is local (0 sends, measured). Close removes the copy (<c>RecyclePhotoModel</c>) and shows
/// the real player again, each step isolated; nothing on the real player needs undoing. The copy is forgotten the moment
/// it is removed, so a recycled model is never read again (Task 4 review carry-over (c)).</summary>
internal sealed partial class ClonePoseModel : IPoseModel, IVisibleModel
{
    private readonly PoseCalls _c;
    private readonly Func<object?> _source;
    private readonly bool _hidden;
    private readonly Action<string> _info;
    private ModelPoser? _p;

    /// <param name="info">The diagnostics line sink (used only with STELLAR_DIAGNOSTICS on).</param>
    public ClonePoseModel(PoseCalls calls, ModelPoser poser, Func<object?> source, bool hidden, Action<string> info)
    {
        _c = calls;
        _p = poser;
        _source = source;
        _hidden = hidden;
        _info = info;
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

    public void Close(PoseTouches touched)
    {
        if (_p is not { } p) return;
        _p = null;   // forgotten before the recycle: nothing reads the copy after this line
        Step("copy", () => { if (p.Live) _c.Actions.Recycle(p.Model); });
        if (!_hidden) { OnPlayerShown("not-hidden"); return; }
        // The restore runs OUTSIDE the diagnostics hook's argument list: a partial method without a body has its call
        // AND its arguments removed by the compiler, so a SetVisible inside the argument would vanish with the hook.
        Step("player", () =>
        {
            var shown = _source() is { } e ? (_c.Spawn.SetVisible(e, true) ? "True" : "False") : "gone";
            OnPlayerShown(shown);
        });
    }

    partial void OnPlayerShown(string shown);

    private void Step(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { _c.Warn($"posing: removing the {what} failed: {(ex.InnerException ?? ex).Message}"); }
    }
}
