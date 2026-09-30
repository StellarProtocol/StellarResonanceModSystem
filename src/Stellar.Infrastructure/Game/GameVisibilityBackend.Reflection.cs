using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>The three game switches (recon rows HUD_HIDE / NAMEPLATE_HIDE / OTHER_PLAYERS_HIDE), resolved by name.</summary>
internal sealed partial class GameVisibilityBackend
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private MethodInfo? _setUiInvisible;   // ZUiRoot.SetUIInvisible(bool)
    private MethodInfo? _setHudSwitch;     // HudMgr.SetHudSwitch(bool, EHudAvailableSource)
    private object? _hudSourceCamera;      // EHudAvailableSource.ECamera
    private MethodInfo? _setEntityShow;    // CameraFrameCtrl.SetEntityShow(int, bool)

    /// <summary>HUD_HIDE: <c>Panda.ZUi.ZUiRoot.Instance.SetUIInvisible(bool)</c> hides the whole game UI canvas.</summary>
    private bool SetGameHudHidden(bool hidden)
    {
        var root = CreatedSingleton(ZUiRootType, "GameHud", out var t);
        if (root is null) return false;
        _setUiInvisible ??= t!.GetMethod("SetUIInvisible", AnyInstance, null, new[] { typeof(bool) }, null);
        if (_setUiInvisible is null) { WarnOnce("m:GameHud", "Hide GameHud unavailable: ZUiRoot.SetUIInvisible not found."); return false; }
        _setUiInvisible.Invoke(root, new object[] { hidden });
        return true;
    }

    /// <summary>NAMEPLATE_HIDE: <c>Panda.Hud.HudMgr.Instance.SetHudSwitch(!hidden, EHudAvailableSource.ECamera)</c>.
    /// The switch is a per-source bitmask, so ours composes with the game's other sources.</summary>
    private bool SetNameplatesHidden(bool hidden)
    {
        var hud = CreatedSingleton(HudMgrType, "Nameplates", out var t);
        if (hud is null) return false;
        if (_setHudSwitch is null)
        {
            var m = StellarInterop.FindMethod(t, "SetHudSwitch", 2);
            var sourceType = m?.GetParameters()[1].ParameterType;
            if (m is null || sourceType is not { IsEnum: true })
            {
                WarnOnce("m:Nameplates", "Hide Nameplates unavailable: HudMgr.SetHudSwitch(bool, EHudAvailableSource) not found.");
                return false;
            }
            _hudSourceCamera = Enum.Parse(sourceType, "ECamera");
            _setHudSwitch = m;
        }
        _setHudSwitch.Invoke(hud, new[] { (object)!hidden, _hudSourceCamera! });
        return true;
    }

    /// <summary>OTHER_PLAYERS_HIDE: <c>Panda.ZGame.CameraFrameCtrl.Instance.SetEntityShow(11, !hidden)</c>, plus
    /// <c>SetEntityShow(3, true)</c> to keep the party. Mechanism proven, effect on other players UNMEASURED (recon).</summary>
    private bool SetOtherPlayersHidden(bool hidden, bool keepParty)
    {
        var ctrl = CreatedSingleton(CameraFrameCtrlType, "OtherPlayers", out var t);
        if (ctrl is null) return false;
        _setEntityShow ??= t!.GetMethod("SetEntityShow", AnyInstance, null, new[] { typeof(int), typeof(bool) }, null);
        if (_setEntityShow is null) { WarnOnce("m:OtherPlayers", "Hide OtherPlayers unavailable: CameraFrameCtrl.SetEntityShow not found."); return false; }
        _setEntityShow.Invoke(ctrl, new object[] { EntityTypeOtherPlayer, !hidden });
        if (hidden && keepParty) _setEntityShow.Invoke(ctrl, new object[] { EntityTypeTeam, true });
        return true;
    }

    /// <summary>
    /// Reads <c>ZSingleton&lt;T&gt;.Instance</c> only when <c>IsCreated</c> says it exists, so a request made before
    /// the game built the singleton (title screen) never makes us construct one. Null = layer unavailable right now.
    /// </summary>
    private object? CreatedSingleton(string typeName, string layer, out Type? type)
    {
        type = _types.FindType(typeName);
        if (type is null)
        {
            WarnOnce("t:" + layer, $"Hide {layer} unavailable: {typeName} not found.");
            return null;
        }
        var isCreated = StellarInterop.FindPropertyUp(type, "IsCreated")?.GetGetMethod(nonPublic: true);
        if (isCreated is { IsStatic: true } && isCreated.Invoke(null, null) is false) return null;
        var instance = StellarInterop.GetSingleton(type);
        if (instance is null) WarnOnce("i:" + layer, $"Hide {layer} unavailable: {typeName}.Instance is not available.");
        return instance;
    }
}
