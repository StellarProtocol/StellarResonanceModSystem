using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// The posing backend's game hooks, installed once on the first open after <see cref="Arm"/>: the photo copy's
/// <c>ZModelManager.cloneModel</c> result postfix (the orphan safety net, regression <c>clone-nre-male-null-ridetpl</c>;
/// pinned by <c>clone_nre_male_null_ridetpl_make_ensure_hooks_*</c>) and the <c>ZEntityMgr.RemoveEntity</c> despawn
/// prefix. A missing type or a failed patch is warned and the rest still installs. Main thread.
/// </summary>
internal sealed class PosingHookSet
{
    private readonly IGameTypeRegistry _types;
    private readonly PhotoCopyMaker _copies;
    private readonly Action<object?, object?[]> _onRemoveEntity;
    private readonly IPluginLog _log;
    private IGameMethodHooks? _hooker;
    private bool _tried;

    public PosingHookSet(IGameTypeRegistry types, PhotoCopyMaker copies, Action<object?, object?[]> onRemoveEntity, IPluginLog log)
    {
        _types = types;
        _copies = copies;
        _onRemoveEntity = onRemoveEntity;
        _log = log;
    }

    /// <summary>Called once the hot-update assemblies are ready; nothing installs until <see cref="Ensure"/>.</summary>
    public void Arm(IGameMethodHooks hooker) => _hooker = hooker;

    /// <summary>Installs every hook once (no-op before <see cref="Arm"/> and after the first armed call).</summary>
    public void Ensure()
    {
        if (_tried || _hooker is null) return;
        _tried = true;
        _copies.Install(_hooker, _types);
        if (_types.FindType(GameEntityAccess.ManagerType) is not { } mgr)
        {
            _log.Warning("[Posing] a person who leaves keeps their copy until the scene ends (ZEntityMgr not found)");
            return;
        }
        try { _hooker.PrefixAllOverloads(mgr, "RemoveEntity", _onRemoveEntity); }
        catch (Exception ex) { _log.Warning("[Posing] despawn hook failed: " + ex.Message); }
    }
}
