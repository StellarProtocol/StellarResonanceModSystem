using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// The game's own local combat setters (recon G): postfixes on <c>EntityAttrExtensions.SetLocalCombatData(ZEntity, bool, long)</c>
/// and <c>SetLocalAttrInBattleShow(ZEntity, bool)</c>, filtered to the local player, installed on first use. The one-shot
/// read is <c>ZEntity.GetLuaIsInCombat()</c>. Each hook fails open with one warning. Main thread.
/// </summary>
internal sealed partial class PandaCombatFlagSource : ICombatFlagSource
{
    private const string Tag = "[FreeCam] ";
    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly IPluginLog _log;
    private readonly LazyHookInstall _hooks = new();
    private MethodInfo? _isInCombat;

    public PandaCombatFlagSource(IGameTypeRegistry types, GameEntityAccess entities, IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _log = log;
    }

    public event Action<CombatFlagKind, bool>? Changed;

    public void ArmHooks(HarmonyGameMethodHooker hooker) => _hooks.Arm(() => Install(hooker));

    public void EnsureHooks() => _hooks.Request();

    public bool? ReadLocalInCombat()
    {
        var entity = _entities.LocalEntity();
        if (entity is null) return null;
        _isInCombat ??= StellarInterop.FindMethod(entity.GetType(), "GetLuaIsInCombat", 0);
        try { return _isInCombat?.Invoke(entity, null) as bool?; }
        catch { return null; }
    }

    private void Install(HarmonyGameMethodHooker hooker)
    {
        var ext = _types.FindType(GameEntityAccess.AttrExtType);
        if (ext is null) { _log.Warning(Tag + "combat-flag hooks unavailable (EntityAttrExtensions not found)"); return; }
        Hook(hooker, ext, "SetLocalCombatData", CombatFlagKind.LocalCombatData);
        Hook(hooker, ext, "SetLocalAttrInBattleShow", CombatFlagKind.InBattleShow);
    }

    private void Hook(HarmonyGameMethodHooker hooker, Type ext, string method, CombatFlagKind kind)
    {
        try
        {
            hooker.PostfixStaticOverloads(ext, method, (_, args) =>
            {
                if (args.Length < 2 || args[1] is not bool on || !IsLocal(args[0])) return;
                OnFlag(kind, on);
                Changed?.Invoke(kind, on);
            });
        }
        catch (Exception ex) { _log.Warning(Tag + $"combat-flag hook {method} failed: {ex.Message}"); }
    }

    private bool IsLocal(object? entity)
    {
        if (entity is null) return false;
        try { return _entities.Uuid(entity) == _entities.PlayerUuid(); }
        catch { return false; }
    }

    partial void OnFlag(CombatFlagKind kind, bool on);
}
