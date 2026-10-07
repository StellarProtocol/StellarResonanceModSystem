using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The game signals <see cref="GameCharacterHides"/> reacts to — all postfixes on reference-type /
/// int-sized arguments only (no <see cref="Il2CppPatchSafety"/> hazard), installed the first time a layer needs them so a
/// session that never uses these switches pays nothing.</summary>
internal sealed partial class GameCharacterHides
{
    private const string WeaponModelCompType = "WeaponModelComp";   // global namespace in Panda.Script
    private const string ModelCompType = "Panda.ZGame.ZModelComp";

    private HarmonyGameMethodHooker? _hooker;
    private bool _hooked;
    private Func<object, object?>? _compModel, _modelHost;

    /// <summary>Remembers the hooker (hot-update ready); the patches go in on first need (<see cref="EnsureHooks"/>).
    /// <c>STELLAR_PHOTO_EAGER_HOOKS=1</c> installs them right away — a TEST-client check that every patch applies (the
    /// title screen needs no login); each callback returns at once while nothing is requested.</summary>
    public void InstallHooks(HarmonyGameMethodHooker hooker)
    {
        _hooker = hooker;
        if (_requested != VisibilityLayers.None || Environment.GetEnvironmentVariable("STELLAR_PHOTO_EAGER_HOOKS") == "1")
            EnsureHooks();
    }

    private void EnsureHooks()
    {
        if (_hooked || _hooker is not { } hooker) return;
        _hooked = true;
        try
        {
            // ZEntityMgr.AddEntity / onAddEntity → checkIsRenderVisible(entity): the game's own "entity entered, apply the
            // active hides" point (it ends in applyCharHideForActiveSources for characters).
            if (_types.FindType(GameEntityAccess.ManagerType) is { } mgr)
                hooker.PostfixAllOverloads(mgr, "checkIsRenderVisible", (_, args) => OnEntityAdded(args.Length > 0 ? args[0] : null));
            // WeaponModelComp.loadWeapon → onWeaponModelLoaded(model): every weapon model the game builds or rebuilds.
            if (_types.FindType(WeaponModelCompType) is { } weapon)
                hooker.PostfixAllOverloads(weapon, "onWeaponModelLoaded", (comp, _) => OnWeaponModelLoaded(comp));
            // The friend / guild caches LuaDataMgr.IsFriend / IsUnionMember read (union_vm.lua, friends_main_vm.lua).
            if (_types.FindType(LuaDataMgrType) is { } lua)
                foreach (var name in new[] { "InitFriendIdCache", "UpdateFriendIdCache", "SyncUnionMemberCharId", "SyncAllUnionMemberCharId" })
                    hooker.PostfixAllOverloads(lua, name, (_, _) => OnMembershipCache());
            _compModel = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(_types.FindType(ModelCompType), "Model"));
            _modelHost = FastAccess.Getter<object?>(StellarInterop.FindPropertyUp(_types.FindType(GameEntityAccess.ModelType), "Host"));
            _log.Info(Tag + "per-character hide hooks installed");
        }
        catch (Exception ex) { WarnOnce("hooks", "per-character hide hooks not installed: " + ex.Message); }
    }

    private void OnEntityAdded(object? arg)
    {
        if (_requested == VisibilityLayers.None) return;
        if (!OnMainThread) { _membershipDirty = true; return; }
        if (_entities.Live(arg) is not { } entity) return;
        var uuid = _entities.Uuid(entity);
        if (!IsPlayerUuid(uuid)) return;
        var local = _entities.PlayerUuid();
        if (uuid == local)
        {
            LocalPlayerRebuilt?.Invoke();
            OnLocalRebuilt("entity added");
            return;
        }
        if (!CharacterHideRules.Needed(_requested) || !Resolve()) return;
        var relation = RelationOf(entity, uuid, local);
        Step(entity, uuid, relation);
        OnEntityStepped(uuid, relation);
    }

    private void OnWeaponModelLoaded(object? comp)
    {
        if ((_requested & VisibilityLayers.Weapons) == 0 || comp is null || _compModel is null || _modelHost is null) return;
        if (!OnMainThread) { _membershipDirty = true; return; }   // a full pass re-hides every weapon too
        long uuid;
        try
        {
            if (_compModel(comp) is not { } model || _entities.Live(_modelHost(model)) is not { } host) return;   // a UI / preview model has no host
            uuid = _entities.Uuid(host);
        }
        catch { return; }
        if (!IsPlayerUuid(uuid)) return;
        if (uuid == _entities.PlayerUuid())
        {
            LocalPlayerRebuilt?.Invoke();
            OnLocalRebuilt("weapon model loaded");
            return;
        }
        _weaponReapply.Add(uuid);   // next tick: the game's load callback (setModelAlpha / weaponAdapter) finishes first
    }

    private void OnMembershipCache()
    {
        MarkMembershipChanged();
        OnMembershipChangedLogged("friend/guild cache");
    }

    partial void OnEntityStepped(long uuid, CharacterRelation relation);
    partial void OnLocalRebuilt(string why);
    partial void OnMembershipChangedLogged(string why);
}
