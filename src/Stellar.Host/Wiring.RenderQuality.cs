using System;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Rendering;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Render quality + time of day (Wiring.RenderQuality.cs) — IRenderQuality / ITimeOfDay ──
    // Spec docs/superpowers/specs/2026-10-01-photo-studio-render-quality-design.md (devkit).
    private const string CaptureScaleGuardEnvVar = "STELLAR_CAPTURE_SCALE_GUARD";
    private const string NoApplyAllDataEnvVar = "STELLAR_RQ_NO_APPLYALLDATA";
    private RenderQualityService? _renderQuality;
    private TimeOfDayService? _timeOfDay;
    private ZRenderQualityBackend? _qualityBackend;
    private LuaTimeOfDayBackend? _timeBackend;
    // Armed by game events, drained on the first framework tick in a stable world scene (never a timer).
    private readonly ReassertGate _qualityReassert = new();
    private readonly ReassertGate _timeReassert = new();

    /// <summary>
    /// Constructs both arbiters. Runs in <c>Load()</c> before <see cref="WirePhotoStudio"/> (the capture service takes
    /// the render-scale guard) — game types resolve lazily; the hooks install from <see cref="InstallRenderQualityHooks"/>.
    /// Re-assert triggers: scene / phase change, the game's quality-apply and time-of-day calls, and (wired in
    /// <see cref="WireRenderQualityPhotoSignals"/>) the game's photo mode or a cutscene ending.
    /// </summary>
    private void WireRenderQuality(BepInExPluginLog log)
    {
        _qualityBackend = new ZRenderQualityBackend(_gameTypeRegistry!, _clientState!, log);
        _renderQuality = new RenderQualityService(_qualityBackend);
        _timeBackend = new LuaTimeOfDayBackend(_gameTypeRegistry!, _clientState!, log);
        _timeOfDay = new TimeOfDayService(_timeBackend);
        _qualityBackend.GameApplied += _qualityReassert.Request;
        _timeBackend.GameChanged += _timeReassert.Request;
        _clientState!.SceneChanged += _ => RequestRenderReassert();
        _clientState.PhaseChanged += _ => RequestRenderReassert();
        _framework!.Update += _ => DrainRenderReassert();
    }

    private void RequestRenderReassert()
    {
        _qualityReassert.Request();
        _timeReassert.Request();
    }

    // Main thread (framework tick). Both gates stay armed through a zone load and fire once the world is stable.
    private void DrainRenderReassert()
    {
        var ready = _clientState!.IsWorldActive;
        if (_qualityReassert.TryTake(ready)) _renderQuality!.Reassert();
        if (_timeReassert.TryTake(ready)) _timeOfDay!.Reassert();
    }

    /// <summary>Cutscene timelines and the game's photo mode set the clock themselves: re-pin after either ends.</summary>
    private void WireRenderQualityPhotoSignals()
    {
        _photoMode!.Exited += _timeReassert.Request;
        _photoMode.CutsceneChanged += on => { if (!on) _timeReassert.Request(); };
    }

    /// <summary>
    /// Capture render-scale guard (spec § 4) — OFF unless <c>STELLAR_CAPTURE_SCALE_GUARD=1</c> (read once at boot),
    /// until the in-game measurement shows the off-screen capture render is multiplied by the render scale.
    /// </summary>
    private Func<IDisposable?>? CaptureScaleGuard(BepInExPluginLog log)
    {
        if (Environment.GetEnvironmentVariable(CaptureScaleGuardEnvVar) != "1") return null;
        log.Info("[PhotoQuality] capture render-scale guard ON (" + CaptureScaleGuardEnvVar + "=1)");
        return _renderQuality!.SuspendSupersampleForCapture;
    }

    // Arms (does not install) the game hooks: they install on the first Request / Pin. Kill switch for the by-ref
    // ApplyAllData hook: STELLAR_RQ_NO_APPLYALLDATA=1 (read once here).
    private void InstallRenderQualityHooks(HarmonyGameMethodHooker hooker)
    {
        _qualityBackend?.ArmHooks(hooker, skipApplyAllData: Environment.GetEnvironmentVariable(NoApplyAllDataEnvVar) == "1");
        _timeBackend?.ArmHooks(hooker);
    }
}
