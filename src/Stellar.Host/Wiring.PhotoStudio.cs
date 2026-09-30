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
    // Set in BuildInfraServices; read lazily by the focus meter (constructed later than the photo services).
    private EntityTransformsService? _entityTransforms;

    /// <summary>
    /// Constructs the four photo services. Runs in <c>Load()</c> before <see cref="ConstructPluginServices"/> — every
    /// game-facing adapter resolves its game types lazily, so nothing here touches a hot-update type yet; the
    /// photo-mode hooks install later from <see cref="InstallPhotoModeHooks"/> (OnHotUpdateReady).
    /// </summary>
    private void WirePhotoStudio(BepInExPluginLog log)
    {
        _sceneVisibility = new SceneVisibilityService(new GameVisibilityBackend(_gameTypeRegistry!, OverlayRoots, log));
        _renderLook = new RenderLookService(
            new ZRenderLookBackend(_gameTypeRegistry!, () => LocalPlayerFocus.Measure(_entityTransforms, _combatService), log),
            m => log.Warning("[PhotoStudio] " + m));
        _screenCapture = new ScreenCaptureService(new UnityFrameGrabber(log), _sceneVisibility, new CaptureFileSink(),
            m => log.Warning("[PhotoStudio] capture: " + m));
        _photoModeProbe = new PandaPhotoModeProbe(_gameTypeRegistry!, log);
        _photoMode = new PhotoModeService(_photoModeProbe);
        var renderLook = _renderLook;
        _framework!.Update += _ => renderLook.Tick();   // no-op unless a look keeps focus on the local player
    }

    private void InstallPhotoModeHooks(HarmonyGameMethodHooker hooker) => _photoModeProbe?.Install(hooker);

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
