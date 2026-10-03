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
    // Panda.ZGame.EVisibleSource.ETakePhotos — the source every CameraFrameCtrl.SetEntityShow case writes (recon).
    private const int PhotoVisibleSource = 4;
    private const string ZEntityMgrType = "Panda.ZGame.ZEntityMgr";

    private MethodInfo? _setUiInvisible;   // ZUiRoot.SetUIInvisible(bool)
    private MethodInfo? _setHudSwitch;     // HudMgr.SetHudSwitch(bool, EHudAvailableSource)
    private object? _hudSourceStellar;     // (EHudAvailableSource)PrivateHudSource
    private MethodInfo? _setEntityShow;    // CameraFrameCtrl.SetEntityShow(int, bool)
    private MethodInfo? _forceRefreshCharVisible;   // ZEntityMgr.ForceRefreshCharVisible(EVisibleSource, bool)
    private object? _photoVisibleSource;            // (EVisibleSource)ETakePhotos — the source SetEntityShow drives
    private MethodInfo? _getHideCount;              // ZEntityMgr.getHideCount(EntityRenderLayerHideType, EVisibleSource)
    private Type? _hideTypeEnum;                    // EntityRenderLayerHideType
    private object? _holdCountSource;               // (EVisibleSource)ETakePhotos for getHideCount
    // Set by InstallHooks (called once hot-update is ready): from then on a missing TYPE is a definitive negative
    // (a game patch renamed/removed it), cached under the same TTL instead of re-probed optimistically every call.
    private bool _hotUpdateReady;
    // Each hide target's resolved Type + static IsCreated getter, so a hide/re-assert re-walks no base chain.
    // Cleared with the negative probes (the same re-init boundaries).
    private readonly System.Collections.Generic.Dictionary<string, (Type Type, MethodInfo? IsCreated)> _singletons = new(StringComparer.Ordinal);

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
    /// Observes the game's re-init points (<c>ZUiRoot.Init</c>: a rebuilt UI root loses our SetUIInvisible; HudMgr;
    /// CameraFrameCtrl). The entity-show switches are refcounts in ZEntityMgr that compose with the game's own
    /// writes, so the game's <c>SetEntityShow</c> calls are deliberately NOT observed (recon-party-grain.md). Call once, after the
    /// hot-update assemblies load. The same re-init points also clear the bounded negative probe caches (scene
    /// change / hot-update-ready is exactly the "session boundary" docs/il2cpp-probing-safety.md calls for) —
    /// clearing all three on any one of them is deliberately coarse: a spurious extra reflection lookup costs
    /// nothing, an under-clear risks re-latching stale unavailability past a fix.
    /// </summary>
    public void InstallHooks(Stellar.Infrastructure.Hooks.HarmonyGameMethodHooker hooker)
    {
        _hotUpdateReady = true;
        ClearNegativeProbes();   // anything cached before ready was an optimistic miss — start clean
        try
        {
            if (_types.FindType(ZUiRootType) is { } ui) hooker.PostfixAllOverloads(ui, "Init", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
            // HudMgr owns hudDisabledFlag_ (our nameplate bit): a re-init or scene entry may reset it.
            if (_types.FindType(HudMgrType) is { } hud)
            {
                hooker.PostfixAllOverloads(hud, "Init", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
                hooker.PostfixAllOverloads(hud, "OnEnterScene", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
            }
            // CameraFrameCtrl re-init: re-probe + re-assert. The entity-show counters live in ZEntityMgr, so held types
            // are NOT re-hidden (a second hide would need a second show) — EntityShowPlan never double-hides.
            if (_types.FindType(CameraFrameCtrlType) is { } cfi)
                hooker.PostfixAllOverloads(cfi, "Init", (_, _) => { ClearNegativeProbes(); TargetRebuilt?.Invoke(); });
            _effects?.InstallHooks(hooker);
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
        _singletons.Clear();
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
        | (ProbeOtherPlayers() ? VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty | VisibilityLayers.Self : VisibilityLayers.None)
        | (_effects?.Available ?? VisibilityLayers.None);

    private bool ProbeGameHud()
    {
        if (_setUiInvisible is not null) return true;
        if (_gameHudNegative.IsSuppressed) return false;
        var t = _types.FindType(ZUiRootType);
        var method = t?.GetMethod("SetUIInvisible", AnyInstance, null, new[] { typeof(bool) }, null);
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: method is not null, _hotUpdateReady))
        {
            case ProbeOutcome.Available:
                _setUiInvisible = method;
                if (_gameHudNegative.MarkRecovered()) _log.Info(Tag + "GameHud recovered: ZUiRoot.SetUIInvisible resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_gameHudNegative.MarkNegative()) _log.Warning(Tag + $"GameHud unavailable: {Missing(t, ZUiRootType, "SetUIInvisible(bool)")} not found.");
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
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: memberFound, _hotUpdateReady))
        {
            case ProbeOutcome.Available:
                _hudSourceStellar = Enum.ToObject(sourceType!, PrivateHudSource);
                _setHudSwitch = m;
                if (_nameplatesNegative.MarkRecovered()) _log.Info(Tag + "Nameplates recovered: HudMgr.SetHudSwitch resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_nameplatesNegative.MarkNegative()) _log.Warning(Tag + $"Nameplates unavailable: {Missing(t, HudMgrType, "SetHudSwitch(bool, EHudAvailableSource)")} not found.");
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
        switch (VisibilityProbeDecision.Decide(typeLoaded: t is not null, memberFound: method is not null, _hotUpdateReady))
        {
            case ProbeOutcome.Available:
                _setEntityShow = method;
                if (_otherPlayersNegative.MarkRecovered()) _log.Info(Tag + "OtherPlayers recovered: CameraFrameCtrl.SetEntityShow resolved.");
                return true;
            case ProbeOutcome.DefinitivelyUnavailable:
                if (_otherPlayersNegative.MarkNegative()) _log.Warning(Tag + $"OtherPlayers unavailable: {Missing(t, CameraFrameCtrlType, "SetEntityShow(int, bool)")} not found.");
                return false;
            default: return true;
        }
    }

    // "Type" when the type itself is gone (post-ready), "Type.Member" when only the member is.
    private static string Missing(Type? t, string typeName, string member) => t is null ? typeName : typeName + "." + member;

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

    /// <summary>OTHER_PLAYERS_HIDE: <c>Panda.ZGame.CameraFrameCtrl.Instance.SetEntityShow(type, show)</c> — OtherPlayer
    /// (11) to hide everyone, Stranger/Chum/Union (6/2/4) to keep the party — driven by <see cref="EntityShowPlan"/>
    /// (one show per hide we issued; refcount semantics from the disassembly, recon-party-grain.md).</summary>
    private bool SetOtherPlayersHidden(bool hidden, bool keepParty)
    {
        if (EntityShowWriter("OtherPlayers") is not { } write) return false;
        return _entityShow.Apply(hidden, keepParty, write, HoldCount);
    }

    /// <summary>Shared by <see cref="SetOtherPlayersHidden"/> and <c>GameVisibilityBackend.Self.cs</c>'s
    /// SetSelfHidden: resolves CameraFrameCtrl + its cached SetEntityShow method (once, via <see cref="_setEntityShow"/>)
    /// and hands back the write closure both <see cref="EntityShowPlan"/> instances call through — one less copy of
    /// the "resolve singleton, cache the method, warn once, write + OnEntityShowWritten" sequence to keep in sync.
    /// Null when the singleton or method can't be resolved right now (the caller's layer is reported not hidden).</summary>
    private Func<int, bool, bool>? EntityShowWriter(string layerTag)
    {
        var ctrl = CreatedSingleton(CameraFrameCtrlType, layerTag, out var t);
        if (ctrl is null) return null;
        _setEntityShow ??= t!.GetMethod("SetEntityShow", AnyInstance, null, new[] { typeof(int), typeof(bool) }, null);
        if (_setEntityShow is null)
        {
            WarnOnce("m:" + layerTag, $"Hide {layerTag} unavailable: CameraFrameCtrl.SetEntityShow not found.");
            return null;
        }
        var setter = _setEntityShow;
        return (type, show) =>
        {
            setter.Invoke(ctrl, new object[] { type, show });
            OnEntityShowWritten(type, show);
            return true;
        };
    }

    /// <summary>
    /// Live hold count behind a camera entity-show type: <c>ZEntityMgr.Instance.getHideCount(hideType, ETakePhotos)</c>
    /// (private in the game, public in the interop). Null when it cannot be read — the plan then keeps its own
    /// bookkeeping. Lets the plan notice a counter reset that silently dropped our hide.
    /// </summary>
    private int? HoldCount(int cameraType)
    {
        try
        {
            if (EntityShowPlan.TryHideTypeFor(cameraType) is not { } hideType) return null;
            if (!EnsureHoldCountReflection(out var mgr)) return null;
            var hide = Enum.ToObject(_hideTypeEnum!, hideType);
            return _getHideCount!.Invoke(mgr, new[] { hide, _holdCountSource! }) is int n ? n : null;
        }
        catch (Exception ex)
        {
            WarnOnce("x:HoldCount", "Hold-count check failed: " + (ex.InnerException ?? ex).Message);
            return null;
        }
    }

    /// <summary>
    /// Resolves (once) the ZEntityMgr singleton + its <c>getHideCount(EntityRenderLayerHideType, EVisibleSource)</c>
    /// method, caching <see cref="_getHideCount"/> / <see cref="_hideTypeEnum"/> / <see cref="_holdCountSource"/> for
    /// every later caller — <see cref="HoldCount"/> AND the Self-holds diagnostic (GameVisibilityBackend.Diagnostics.cs),
    /// which needs the same fields but has no camera type to resolve through <see cref="EntityShowPlan.TryHideTypeFor"/>
    /// (Oneself/SelfPet aren't in that map). False (with the same warning as before extraction) when the singleton or
    /// method isn't available right now; callers decide how to react (HoldCount returns null, the diagnostic no-ops).
    /// </summary>
    private bool EnsureHoldCountReflection(out object? mgr)
    {
        mgr = CreatedSingleton(ZEntityMgrType, "HoldCount", out var t);
        if (mgr is null) return false;
        if (_getHideCount is not null) return true;
        var m = StellarInterop.FindMethod(t, "getHideCount", 2);
        var ps = m?.GetParameters();
        if (m is null || ps![0].ParameterType is not { IsEnum: true } hideTypeEnum || ps[1].ParameterType is not { IsEnum: true } sourceType)
        {
            WarnOnce("m:HoldCount", "Hold-count check unavailable: ZEntityMgr.getHideCount not found.");
            return false;
        }
        _hideTypeEnum = hideTypeEnum;
        _holdCountSource = Enum.ToObject(sourceType, PhotoVisibleSource);
        _getHideCount = m;
        return true;
    }

    /// <summary>
    /// While the keep-party set is held, asks the game to re-evaluate every character's photo-source visibility
    /// (<c>ZEntityMgr.Instance.ForceRefreshCharVisible(ETakePhotos, false)</c>): party membership is read live, so a
    /// member who joined (or left) during the hide is shown (or hidden) without waiting for the next switch write.
    /// No-op otherwise; fails open with one warning. Main thread.
    /// </summary>
    public void RefreshPartyVisibility()
    {
        if (!_entityShow.HoldsKeepPartySet) return;
        try
        {
            var mgr = CreatedSingleton(ZEntityMgrType, "PartyRefresh", out var t);
            if (mgr is null) return;
            if (_forceRefreshCharVisible is null)
            {
                var m = StellarInterop.FindMethod(t, "ForceRefreshCharVisible", 2);
                var sourceType = m?.GetParameters()[0].ParameterType;
                if (m is null || sourceType is not { IsEnum: true })
                {
                    WarnOnce("m:PartyRefresh", "Party visibility refresh unavailable: ZEntityMgr.ForceRefreshCharVisible not found.");
                    return;
                }
                _photoVisibleSource = Enum.ToObject(sourceType, PhotoVisibleSource);
                _forceRefreshCharVisible = m;
            }
            _forceRefreshCharVisible.Invoke(mgr, new[] { _photoVisibleSource!, (object)false });
        }
        catch (Exception ex) { WarnOnce("x:PartyRefresh", "Party visibility refresh failed: " + (ex.InnerException ?? ex).Message); }
    }

    /// <summary>
    /// Reads <c>ZSingleton&lt;T&gt;.Instance</c> only when <c>IsCreated</c> says it exists, so a request made before
    /// the game built the singleton (title screen) never makes us construct one. Null = layer unavailable right now.
    /// </summary>
    private object? CreatedSingleton(string typeName, string layer, out Type? type)
    {
        if (!_singletons.TryGetValue(typeName, out var target))
        {
            var t = _types.FindType(typeName);
            if (t is null)
            {
                type = null;
                WarnOnce("t:" + layer, $"Hide {layer} unavailable: {typeName} not found.");
                return null;
            }
            target = (t, StellarInterop.FindPropertyUp(t, "IsCreated")?.GetGetMethod(nonPublic: true));
            _singletons[typeName] = target;
        }
        type = target.Type;
        if (target.IsCreated is { IsStatic: true } isCreated && isCreated.Invoke(null, null) is false) return null;
        var instance = StellarInterop.GetSingleton(type);
        if (instance is null) WarnOnce("i:" + layer, $"Hide {layer} unavailable: {typeName}.Instance is not available.");
        return instance;
    }
}
