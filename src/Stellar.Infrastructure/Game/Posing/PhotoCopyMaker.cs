using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Makes the game's photo copy of another player without tripping the game's own crash (regression id
/// <c>clone-nre-male-null-ridetpl</c>, probe run 6): first the <see cref="CloneGuard"/> normalises an idle Male source's
/// null ride template to <c>""</c>, then <c>CloneModelForPhoto</c> runs inside <see cref="CloneOrphanNet"/>, so a copy the
/// game registered before throwing is removed instead of leaking. The <c>ZModelManager.cloneModel</c> result postfix is
/// installed with the posing hooks on the first open; it does nothing unless our own clone call is running. Main thread.
/// </summary>
internal sealed partial class PhotoCopyMaker
{
    internal const string ModelManagerType = "Panda.ZGame.ZModelManager";

    private readonly PoseActionCalls _actions;
    private readonly RideTemplateCalls _ride;
    private readonly IPluginLog _log;
    private readonly CloneOrphanNet _net = new();

    public PhotoCopyMaker(PoseActionCalls actions, RideTemplateCalls ride, IPluginLog log)
    {
        _actions = actions;
        _ride = ride;
        _log = log;
    }

    /// <summary>Installs the <c>cloneModel</c> result postfix (the orphan safety net). Once, with the posing hooks.</summary>
    public void Install(HarmonyGameMethodHooker hooker, IGameTypeRegistry types)
    {
        if (types.FindType(ModelManagerType) is not { } mgr)
        {
            _log.Warning("[Posing] ZModelManager not found: a failed photo copy cannot be cleaned up");
            return;
        }
        try { hooker.PostfixResultAllOverloads(mgr, "cloneModel", _net.Record); }
        catch (Exception ex) { _log.Warning("[Posing] photo-copy cleanup hook failed: " + ex.Message); }
    }

    /// <summary>The copy of <paramref name="entity"/>, or null when the game is not ready. A game exception propagates
    /// (the caller's "could not prepare" path) after any half-made copy was removed.</summary>
    public object? Make(object entity, object? sourceModel)
    {
        if (sourceModel is not null) Guard(entity, sourceModel);
        OnCloning();
        try { return _net.Run(() => _actions.Clone(entity), o => _actions.Recycle(o), m => _log.Warning("[Posing] " + m)); }
        finally { OnCloned(); }
    }

    // A failed check never blocks the copy: the orphan net still covers a crash.
    private void Guard(object entity, object model)
    {
        try
        {
            if (_ride.Normalise(entity, model) is { } reading) OnGuardRead(reading);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Posing] the photo-copy ride-template check failed: {(ex.InnerException ?? ex).Message}");
        }
    }

    partial void OnGuardRead(CloneGuardReading reading);
    partial void OnCloning();
    partial void OnCloned();
}
