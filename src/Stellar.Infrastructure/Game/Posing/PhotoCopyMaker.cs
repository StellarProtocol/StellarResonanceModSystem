using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Makes the game's photo copy of another player without tripping the game's own crash (regression id
/// <c>clone-nre-male-null-ridetpl</c>, probe run 6): first the <see cref="CloneGuard"/> normalises an idle Male source's
/// null ride template to <c>""</c> (keeping its fade), then <c>CloneModelForPhoto</c> runs inside
/// <see cref="CloneOrphanNet"/>, so a copy the game registered before throwing is removed instead of leaking. The
/// <c>ZModelManager.cloneModel</c> result postfix is installed with the posing hooks on the first open; it records nothing
/// unless our own clone call is running. Wiring pinned by the <c>clone_nre_male_null_ridetpl_make_*</c> tests. Main thread.
/// </summary>
internal sealed partial class PhotoCopyMaker
{
    internal const string ModelManagerType = "Panda.ZGame.ZModelManager";
    internal const string CloneModelMethod = "cloneModel";

    private readonly IPhotoCopyCalls _actions;
    private readonly IRideTemplateAccess _ride;
    private readonly IPluginLog _log;
    private readonly CloneOrphanNet _net = new();
    private bool _guardOffWarned;

    public PhotoCopyMaker(IPhotoCopyCalls actions, IRideTemplateAccess ride, IPluginLog log)
    {
        _actions = actions;
        _ride = ride;
        _log = log;
    }

    /// <summary>True only while our own clone call runs (the postfix records). Test seam.</summary>
    internal bool CleanupArmed => _net.Armed;

    /// <summary>Installs the <c>cloneModel</c> result postfix (the orphan safety net). Once, with the posing hooks.</summary>
    public void Install(IGameMethodHooks hooker, IGameTypeRegistry types)
    {
        if (types.FindType(ModelManagerType) is not { } mgr)
        {
            _log.Warning("[Posing] ZModelManager not found: a failed photo copy cannot be cleaned up");
            return;
        }
        try { hooker.PostfixResultAllOverloads(mgr, CloneModelMethod, _net.Record); }
        catch (Exception ex) { _log.Warning("[Posing] photo-copy cleanup hook failed: " + ex.Message); }
    }

    /// <summary>The copy of <paramref name="entity"/>, or null when the game is not ready. A game exception propagates
    /// (the caller's "could not prepare" path) after any half-made copy was removed.</summary>
    public object? Make(object entity, object? sourceModel)
    {
        if (sourceModel is not null) Guard(entity, sourceModel);
        OnCloning();
        try { return _net.Run(() => _actions.Clone(entity), _actions.Recycle, m => _log.Warning("[Posing] " + m)); }
        finally { OnCloned(_net.LastRecords); }
    }

    // A failed check never blocks the copy: the orphan net still covers a crash.
    private void Guard(object entity, object model)
    {
        try
        {
            if (Normalise(entity, model) is { } reading) OnGuardRead(reading);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Posing] the photo-copy ride-template check failed: {(ex.InnerException ?? ex).Message}");
        }
    }

    // Reads the source; when the copy would crash, sets its ride template to "" with its ORIGINAL fade. Null (and one
    // warning per session) when the game lacks a member the guard needs.
    private CloneGuardReading? Normalise(object entity, object model)
    {
        if (_ride.Missing() is { } missing)
        {
            if (!_guardOffWarned) _log.Warning($"[Posing] ride-template guard unavailable ({missing} not found)");
            _guardOffWarned = true;
            return null;
        }
        var gender = _ride.Gender(model);
        var state = _ride.State(entity);
        var actionId = _ride.ActionId(model);
        var templateNull = _ride.TemplateIsNull(model);
        var needs = CloneGuard.NeedsRideTemplateNormalise(gender, state, actionId, templateNull);
        if (needs) _ride.SetTemplate(model, string.Empty, _ride.Fade(model));
        return new CloneGuardReading(gender, state, actionId, templateNull, needs);
    }

    partial void OnGuardRead(CloneGuardReading reading);
    partial void OnCloning();
    partial void OnCloned(int records);
}
