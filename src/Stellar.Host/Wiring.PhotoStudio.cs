using System.Collections.Generic;
using Stellar.Application.Imaging;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Rendering;
using UnityEngine;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Photo services (Wiring.PhotoStudio.cs) — IScreenCapture / ISceneVisibility / IRenderLook / IPhotoModeState ──
    private SceneVisibilityService? _sceneVisibility;
    private RenderLookService? _renderLook;
    private ScreenCaptureService? _screenCapture;
    private PhotoModeService? _photoMode;
    private PandaPhotoModeProbe? _photoModeProbe;
    private GameVisibilityBackend? _visibilityBackend;
    private ZRenderLookBackend? _lookBackend;
    private UnityFrameGrabber? _frameGrabber;   // its late resumes are drained from RunGlobalRateWork (main thread, un-gated)
    private bool _photoReassertPending;   // set by game signals, drained on the next framework tick
    // Party roster changed while the keep-party hide is held: refresh character visibility once the world is stable.
    private readonly ReassertGate _partyVisibilityRefresh = new();
    // Set in BuildInfraServices; read lazily by the focus meter (constructed later than the photo services).
    private EntityTransformsService? _entityTransforms;

    /// <summary>
    /// Constructs the four photo services. Runs in <c>Load()</c> before <see cref="ConstructPluginServices"/> — every
    /// game-facing adapter resolves its game types lazily, so nothing here touches a hot-update type yet; the
    /// game hooks install later from <see cref="InstallPhotoModeHooks"/> (OnHotUpdateReady).
    /// </summary>
    private void WirePhotoStudio(BepInExPluginLog log)
    {
        var summons = new SummonOwnerIndex();
        _combatService!.CombatEventOccurred += summons.OnCombatEvent;
        var ownerLookup = new GameSummonerLookup(_gameTypeRegistry!, log);
        _clientState!.SceneChanged += _ => ownerLookup.Clear();   // entity ids are scene-scoped
        var classifier = new EffectOwnerClassifier(() => _combatService.LocalEntityId, () => _partyService!.Members, summons,
            ownerLookup.TopSummonerOf);
        var effects = new GameEffectVisibility(_gameTypeRegistry!, classifier.Classify, log);
        _visibilityBackend = new GameVisibilityBackend(_gameTypeRegistry!, OverlayRoots, log, effects);
        _sceneVisibility = new SceneVisibilityService(_visibilityBackend);
        _lookBackend = new ZRenderLookBackend(_gameTypeRegistry!, () => LocalPlayerFocus.Measure(_entityTransforms, _combatService), log);
        _renderLook = new RenderLookService(_lookBackend, m => log.Warning("[PhotoStudio] " + m));
        _frameGrabber = new UnityFrameGrabber(log, _reShadeBridge);
        _screenCapture = new ScreenCaptureService(_frameGrabber, _sceneVisibility, new CaptureFileSink(),
            m => log.Warning("[PhotoStudio] capture: " + m), CaptureScaleGuard(log), _reShadeService);
        _photoModeProbe = new PandaPhotoModeProbe(_gameTypeRegistry!, _clientState!, log);
        _photoMode = new PhotoModeService(_photoModeProbe);
        WirePhotoReassert();
        WireRenderQualityPhotoSignals();
        var renderLook = _renderLook;
        _framework!.Update += _ => { renderLook.Tick(); DrainPhotoReassert(); };   // Tick is a no-op unless a look tracks the player
    }

    /// <summary>
    /// Our hides share switches with the game's own camera mode / cutscenes, and their targets can be rebuilt.
    /// Game signals (photo-mode exit, cutscene end, ZUiRoot/HudMgr/CameraFrameCtrl init, HudMgr scene entry) re-assert on the NEXT framework tick so the game's
    /// own restore path finishes first; our own canvases re-assert immediately (no one-tick flash). Event-driven only.
    /// </summary>
    private void WirePhotoReassert()
    {
        _photoMode!.Exited += () => _photoReassertPending = true;
        _photoMode.CutsceneChanged += on => { if (!on) _photoReassertPending = true; };
        _visibilityBackend!.TargetRebuilt += () => _photoReassertPending = true;
        var visibility = _sceneVisibility!;
        if (_windowRenderer is not null) _windowRenderer.CanvasCreated += visibility.Reassert;
        if (_layoutOverlay is not null) _layoutOverlay.ChromeCanvasCreated += visibility.Reassert;
        _partyService!.MemberJoined += _ => _partyVisibilityRefresh.Request();
        _partyService.MemberLeft += (_, _) => _partyVisibilityRefresh.Request();
        _partyService.PartyDissolved += _partyVisibilityRefresh.Request;
    }

    private void DrainPhotoReassert()
    {
        if (_partyVisibilityRefresh.TryTake(_clientState!.IsWorldActive)) _visibilityBackend?.RefreshPartyVisibility();
        if (!_photoReassertPending) return;
        _photoReassertPending = false;
        _sceneVisibility?.Reassert();
    }

    private void InstallPhotoModeHooks(HarmonyGameMethodHooker hooker)
    {
        _photoModeProbe?.Install(hooker);
        _visibilityBackend?.InstallHooks(hooker);
        InstallRenderQualityHooks(hooker);
        InstallFreeCameraHooks(hooker);
    }

    private void DisposePhotoStudio()
    {
        _lookBackend?.Dispose();
        DisposeFreeCamera();   // after plugins released their handles (Unload disposes the registry first)
    }

    // The framework's own overlay canvases (HideAndDontSave, so taken from their owners, never searched for).
    // The toast canvas is deliberately absent: toasts stay visible (capture feedback).
    private IReadOnlyList<GameObject> OverlayRoots()
    {
        var roots = new List<GameObject>(2);
        if (_windowRenderer?.CanvasObject is { } window && window != null) roots.Add(window);
        if (_layoutOverlay?.ChromeCanvas is { } chrome && chrome != null) roots.Add(chrome);
        return roots;
    }
}
