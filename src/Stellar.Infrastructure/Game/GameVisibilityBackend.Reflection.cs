using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
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

    // Bounded negative caches for Available's probes (docs/il2cpp-probing-safety.md § negative-cache races: a
    // negative verdict must expire, never latch permanently). N = 5s: long enough that a panel polling Available
    // every frame doesn't re-run FindType/GetMethod more than a couple of times a minute once a layer is known
    // missing, short enough that a hot-update reload mid-session (or a transient miss during boot) self-heals
    // within a few seconds rather than needing a relaunch. A positive resolution is still cached permanently in
    // the setter's own field above (_setUiInvisible etc.) — these three exist only for the negative case, which
    // has nowhere else to live. Cleared on the SAME re-init hooks that already re-assert held hides below.
    private const long NegativeProbeTtlMs = 5_000;
    private readonly NegativeProbeCache _gameHudNegative = new(() => Environment.TickCount64, NegativeProbeTtlMs);
    private readonly NegativeProbeCache _nameplatesNegative = new(() => Environment.TickCount64, NegativeProbeTtlMs);
    private readonly NegativeProbeCache _otherPlayersNegative = new(() => Environment.TickCount64, NegativeProbeTtlMs);

    /// <summary>Raised (main thread) after the game (re)initialised a hide target — <c>ZUiRoot.Init</c>,
    /// <c>HudMgr.Init</c> / <c>HudMgr.OnEnterScene</c>, <c>CameraFrameCtrl.Init</c>.</summary>
    public event Action? TargetRebuilt;

    /// <summary>
    /// Observes the game's own <c>CameraFrameCtrl.SetEntityShow</c> writes (so a release restores the game's value,
    /// not a guess) and <c>ZUiRoot.Init</c> (a rebuilt UI root loses our SetUIInvisible). Call once, after the
    /// hot-update assemblies load. The same re-init points also clear the bounded negative probe caches (scene
    /// change / hot-update-ready is exactly the "session boundary" docs/il2cpp-probing-safety.md calls for) —
    /// clearing all three on any one of them is deliberately coarse: a spurious extra reflection lookup costs
    /// nothing, an under-clear risks re-latching stale unavailability past a fix.
    /// </summary>
    public void InstallHooks(Stellar.Infrastructure.Hooks.HarmonyGameMethodHooker hooker)
    {
        try
        {
            if (_types.FindType(CameraFrameCtrlType) is { } cf) hooker.PostfixAllOverloads(cf, "SetEntityShow", OnGameSetEntityShow);
            else WarnOnce("hook:SetEntityShow", $"Hide OtherPlayers: {CameraFrameCtrlType} not found; game camera-mode writes are not tracked.");
            if (_types.FindType(ZUiRootType) is { } ui) hooker.PostfixAllOverloads(ui, "Init", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
            // HudMgr owns hudDisabledFlag_ (our nameplate bit): a re-init or scene entry may reset it.
            if (_types.FindType(HudMgrType) is { } hud)
            {
                hooker.PostfixAllOverloads(hud, "Init", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
                hooker.PostfixAllOverloads(hud, "OnEnterScene", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
            }
            // A CameraFrameCtrl re-init may reset its entity-show flags: forget the mirror, then re-assert.
            if (_types.FindType(CameraFrameCtrlType) is { } cfi)
                hooker.PostfixAllOverloads(cfi, "Init", (_, _) => { _entityShow.Reset(); ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
        }
        catch (Exception ex) { WarnOnce("hooks", "visibility hooks not installed: " + ex.Message); }
    }

    /// <summary>Forgets every bounded negative probe result — called from the re-init hooks above. A relaunch is
    /// never the only reset available in-game (docs/il2cpp-probing-safety.md rule 4).</summary>
    private void ClearNegativeProbes()
    {
        _gameHudNegative.Reset();
        _nameplatesNegative.Reset();
        _otherPlayersNegative.Reset();
    }

    /// <summary>
    /// Probe-only: resolves the same reflection target each setter below caches, but never invokes it and never
    /// touches game state. Overlay is the framework's own canvas set (no external game reflection involved), so it
    /// is always reported available; a hot-update type not yet loaded is optimistic true (re-probed every call —
    /// nothing to cache yet), and a loaded type missing the expected member is a definitive false, cached so later
    /// calls skip re-resolving it (see <see cref="VisibilityProbeDecision"/>).
    /// </summary>
    public VisibilityLayers Available =>
        (ProbeGameHud() ? VisibilityLayers.GameHud : VisibilityLayers.None)
        | VisibilityLayers.StellarOverlay
        | (ProbeNameplates() ? VisibilityLayers.Nameplates : VisibilityLayers.None)
        | (ProbeOtherPlayers() ? VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty : VisibilityLayers.None);

    private bool ProbeGameHud()
    {
        if (_setUiInvisible is not null) return true;
        if (_gameHudNegative.IsSuppressed) return false;
        var t = _types.FindType(ZUiRootType);
        var method = t?.GetMethod("SetUIInvisible", AnyInstance, null, new[] { typeof(bool) }, null);
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: method is not null))
        {
            case ProbeOutcome.Available:
                _setUiInvisible = method;
                if (_gameHudNegative.MarkRecovered()) _log.Info(Tag + "GameHud recovered: ZUiRoot.SetUIInvisible resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_gameHudNegative.MarkNegative()) _log.Warning(Tag + $"GameHud unavailable: {ZUiRootType}.SetUIInvisible(bool) not found.");
                return false;
            default: return true; // Optimistic — type not loaded yet, nothing to cache
        }
    }

    private bool ProbeNameplates()
    {
        if (_setHudSwitch is not null) return true;
        if (_nameplatesNegative.IsSuppressed) return false;
        var t = _types.FindType(HudMgrType);
        var m = t is null ? null : StellarInterop.FindMethod(t, "SetHudSwitch", 2);
        var sourceType = m?.GetParameters()[1].ParameterType;
        var memberFound = m is not null && sourceType is { IsEnum: true };
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: memberFound))
        {
            case ProbeOutcome.Available:
                _hudSourceStellar = Enum.ToObject(sourceType!, PrivateHudSource);
                _setHudSwitch = m;
                if (_nameplatesNegative.MarkRecovered()) _log.Info(Tag + "Nameplates recovered: HudMgr.SetHudSwitch resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_nameplatesNegative.MarkNegative()) _log.Warning(Tag + $"Nameplates unavailable: {HudMgrType}.SetHudSwitch(bool, EHudAvailableSource) not found.");
                return false;
            default: return true;
        }
    }

    private bool ProbeOtherPlayers()
    {
        if (_setEntityShow is not null) return true;
        if (_otherPlayersNegative.IsSuppressed) return false;
        var t = _types.FindType(CameraFrameCtrlType);
        var method = t?.GetMethod("SetEntityShow", AnyInstance, null, new[] { typeof(int), typeof(bool) }, null);
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: method is not null))
        {
            case ProbeOutcome.Available:
                _setEntityShow = method;
                if (_otherPlayersNegative.MarkRecovered()) _log.Info(Tag + "OtherPlayers recovered: CameraFrameCtrl.SetEntityShow resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_otherPlayersNegative.MarkNegative()) _log.Warning(Tag + $"OtherPlayers unavailable: {CameraFrameCtrlType}.SetEntityShow(int, bool) not found.");
                return false;
            default: return true;
        }
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
