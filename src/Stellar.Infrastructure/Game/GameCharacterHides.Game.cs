using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game;

/// <summary>The game members <see cref="GameCharacterHides"/> writes and reads, resolved by name once (CI builds against
/// the refs/ stubs). Addresses: docs/recon/photo-hide-recon.md § "Per-character hides".</summary>
internal sealed partial class GameCharacterHides
{
    // Our OWN EVisibleSource. ZEntityHelper.setVisible (0x183FC5C40) sets bit (source & 31) of the entity's visibility
    // flag attr (0x53A) and the model is hidden while ANY bit is set. The game declares sources 0..15 (ECommon …
    // AoyiSkill) and writes only those; the flag attr is written nowhere but setVisible, CopyModelRenderVisible and the
    // summon attr sync (static scan for the 0x53A immediate). So bit 28 composes with every game source and nothing in
    // the game clears it — no re-assert needed after the game's photo screen closes.
    internal const int PrivateVisibleSource = 28;
    // EModelAlphaSourceType.EPhoto. Every game writer (SetEntityShow 10, ResetCameraInitialParameters, the Lua bridge's
    // ChangePlayerWeaponVisible / SetModelRenderVisible) targets PlayerEnt only, so on another player it is ours alone.
    internal const int PhotoAlphaSource = 1;
    // ZModelHelper.SetVisilbe render mask WeaponComp.ChangeWeaponVisible passes: the weapon model + weapon effects.
    internal const uint WeaponRenderMask = 0x6000;
    // EntityId marker of a player character (uuid low 16 bits) — EntityId.IsPlayer.
    private const long PlayerMarker = 640;

    private const string HelperType = "Panda.ZGame.ZEntityHelper";
    private const string ModelHelperType = "Panda.ZGame.ZModelHelper";
    private const string LuaDataMgrType = "Panda.ZUi.LuaDataMgr";
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly object True = true;
    private static readonly object False = false;
    private static readonly object WeaponMask = WeaponRenderMask;

    private readonly IGameTypeRegistry _types;
    private readonly SingletonAccess _luaData = new();
    private readonly object?[] _bodyArgs = new object?[6];
    private readonly object?[] _weaponArgs = new object?[5];
    private Func<object, long>? _charId;
    private Func<object, long, bool>? _isFriend, _isGuildMate;
    private MethodInfo? _setModelRenderVisible, _setVisilbe;
    private object? _privateSource, _photoAlpha;

    private bool Resolve()
    {
        if (_setVisilbe is not null) return true;
        var entity = _types.FindType(GameEntityAccess.EntityType);
        var helper = _types.FindType(HelperType);
        var modelHelper = _types.FindType(ModelHelperType);
        var lua = _types.FindType(LuaDataMgrType);
        var render = StellarInterop.FindMethod(helper, "SetModelRenderVisible", 6);
        var visilbe = StellarInterop.FindMethod(modelHelper, "SetVisilbe", 5);
        var charId = StellarInterop.FindPropertyUp(entity, "CharId");
        if (lua is null || !_luaData.Resolve(lua) || render?.GetParameters()[2].ParameterType is not { IsEnum: true } source ||
            visilbe?.GetParameters()[3].ParameterType is not { IsEnum: true } alpha || charId?.PropertyType != typeof(long))
        {
            WarnOnce("resolve", "per-character hides unavailable: ZEntityHelper.SetModelRenderVisible / ZModelHelper.SetVisilbe / " +
                                "LuaDataMgr / ZEntity.CharId not found — friends, party, guild and other players' weapons stay visible.");
            return false;
        }
        _charId = FastAccess.Getter<long>(charId);
        _isFriend = FastAccess.Func1<long, bool>(lua.GetMethod("IsFriend", AnyInstance, null, new[] { typeof(long) }, null));
        _isGuildMate = FastAccess.Func1<long, bool>(lua.GetMethod("IsUnionMember", AnyInstance, null, new[] { typeof(long) }, null));
        _privateSource = Enum.ToObject(source, PrivateVisibleSource);
        _photoAlpha = Enum.ToObject(alpha, PhotoAlphaSource);
        _setModelRenderVisible = render;
        _setVisilbe = visilbe;   // set last: the "fully resolved" sentinel
        return true;
    }

    /// <summary><c>ZEntityHelper.SetModelRenderVisible(entity, !hide, (EVisibleSource)28, withSummon:false,
    /// withEffect:true, triggerNow:false)</c> — the same flags the game's own photo pass uses for characters.</summary>
    private bool SetBodyHidden(object entity, bool hide)
    {
        _bodyArgs[0] = entity;
        _bodyArgs[1] = hide ? False : True;
        _bodyArgs[2] = _privateSource;
        _bodyArgs[3] = False;
        _bodyArgs[4] = True;
        _bodyArgs[5] = False;
        return Invoke(_setModelRenderVisible!, _bodyArgs, "body");
    }

    /// <summary><c>ZModelHelper.SetVisilbe(entity.Model, 0x6000, !hide, EPhoto, setEffectAlpha:true)</c>. No live model
    /// = a hide fails (not held; the weapon-model hook re-applies when it loads) and a show has nothing to do.</summary>
    private bool SetWeaponHidden(object entity, bool hide)
    {
        if (_entities.LiveModel(entity) is not { } model) return !hide;
        _weaponArgs[0] = model;
        _weaponArgs[1] = WeaponMask;
        _weaponArgs[2] = hide ? False : True;
        _weaponArgs[3] = _photoAlpha;
        _weaponArgs[4] = True;
        return Invoke(_setVisilbe!, _weaponArgs, "weapon");
    }

    private bool Invoke(MethodInfo method, object?[] args, string what)
    {
        try
        {
            method.Invoke(null, args);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("x:" + what, $"per-character {what} hide failed: {(ex.InnerException ?? ex).Message}");
            return false;
        }
        finally { args[0] = null; }   // never keep a game object alive between calls
    }

    private long CharIdOf(object entity)
    {
        try { return _charId!(entity); }
        catch { return 0; }
    }

    private bool IsFriend(long uuid) => AskLuaData(_isFriend, uuid);
    private bool IsGuildMate(long uuid) => AskLuaData(_isGuildMate, uuid);

    private bool AskLuaData(Func<object, long, bool>? query, long uuid)
    {
        if (query is null || _luaData.Get() is not { } mgr) return false;
        try { return query(mgr, uuid); }
        catch { return false; }
    }

    private static bool IsPlayerUuid(long uuid) => (uuid & 0xFFFF) == PlayerMarker;
}
