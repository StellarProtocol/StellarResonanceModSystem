using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Game;

/// <summary>The three game switches (recon rows HUD_HIDE / NAMEPLATE_HIDE / OTHER_PLAYERS_HIDE), resolved by name.</summary>
internal sealed partial class GameVisibilityBackend
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // Our OWN bit in HudMgr's per-source disable mask. The enum defines EUi=1 EGm=2 ECamera=4 ECutScene=8
    // EInteraction=16; ECamera is what the game's camera mode itself uses, so sharing it let the game's exit
    // re-show nameplates we hold (and ours re-show the game's). An unused high bit composes with every game
    // source (fix round 1, measured in-game — see docs/recon/photo-studio-render-recon.md).
    internal const int PrivateHudSource = 1 << 10;

    private MethodInfo? _setUiInvisible;   // ZUiRoot.SetUIInvisible(bool)
    private MethodInfo? _setHudSwitch;     // HudMgr.SetHudSwitch(bool, EHudAvailableSource)
    private object? _hudSourceStellar;     // (EHudAvailableSource)PrivateHudSource
    private MethodInfo? _setEntityShow;    // CameraFrameCtrl.SetEntityShow(int, bool)
    private bool _selfEntityShowWrite;     // true while WE call SetEntityShow, so the observer ignores it

    /// <summary>Raised (main thread) after the game (re)initialised a hide target — <c>ZUiRoot.Init</c>.</summary>
    public event Action? TargetRebuilt;

    /// <summary>
    /// Observes the game's own <c>CameraFrameCtrl.SetEntityShow</c> writes (so a release restores the game's value,
    /// not a guess) and <c>ZUiRoot.Init</c> (a rebuilt UI root loses our SetUIInvisible). Call once, after the
    /// hot-update assemblies load.
    /// </summary>
    public void InstallHooks(Stellar.Infrastructure.Hooks.HarmonyGameMethodHooker hooker)
    {
        try
        {
            if (_types.FindType(CameraFrameCtrlType) is { } cf) hooker.PostfixAllOverloads(cf, "SetEntityShow", OnGameSetEntityShow);
            else WarnOnce("hook:SetEntityShow", $"Hide OtherPlayers: {CameraFrameCtrlType} not found; game camera-mode writes are not tracked.");
            if (_types.FindType(ZUiRootType) is { } ui) hooker.PostfixAllOverloads(ui, "Init", (_, _) => TargetRebuilt?.Invoke());
        }
        catch (Exception ex) { WarnOnce("hooks", "visibility hooks not installed: " + ex.Message); }
    }

    private void OnGameSetEntityShow(object? instance, object?[] args)
    {
        if (_selfEntityShowWrite || args.Length < 2 || args[0] is not int type || args[1] is not bool show) return;
        _entityShow.ObserveGame(type, show);
        OnGameEntityShow(type, show);
    }

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

    /// <summary>NAMEPLATE_HIDE: <c>Panda.Hud.HudMgr.Instance.SetHudSwitch(!hidden, (EHudAvailableSource)PrivateHudSource)</c>.
    /// The switch is a per-source bitmask, so our private bit composes with every game source.</summary>
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
            _hudSourceStellar = Enum.ToObject(sourceType, PrivateHudSource);
            _setHudSwitch = m;
        }
        _setHudSwitch.Invoke(hud, new[] { (object)!hidden, _hudSourceStellar! });
        return true;
    }

    /// <summary>OTHER_PLAYERS_HIDE: <c>Panda.ZGame.CameraFrameCtrl.Instance.SetEntityShow(type, show)</c> for
    /// OtherPlayer (11) and Team (3), driven by <see cref="EntityShowPlan"/> so a release restores the game's own
    /// values. Mechanism proven; effect on real other players still UNMEASURED (recon).</summary>
    private bool SetOtherPlayersHidden(bool hidden, bool keepParty)
    {
        var ctrl = CreatedSingleton(CameraFrameCtrlType, "OtherPlayers", out var t);
        if (ctrl is null) return false;
        _setEntityShow ??= t!.GetMethod("SetEntityShow", AnyInstance, null, new[] { typeof(int), typeof(bool) }, null);
        if (_setEntityShow is null) { WarnOnce("m:OtherPlayers", "Hide OtherPlayers unavailable: CameraFrameCtrl.SetEntityShow not found."); return false; }
        var setter = _setEntityShow;
        return _entityShow.Apply(hidden, keepParty, (type, show) =>
        {
            _selfEntityShowWrite = true;
            try { setter.Invoke(ctrl, new object[] { type, show }); }
            finally { _selfEntityShowWrite = false; }
            OnEntityShowWritten(type, show);
            return true;
        });
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
