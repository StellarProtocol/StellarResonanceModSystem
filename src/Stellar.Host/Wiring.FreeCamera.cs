using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Rendering;
using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.Unity;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Free camera (Wiring.FreeCamera.cs) — ICameraOverride / IInputShield / ISceneFreeze / IEmotes / ICombatState /
    //    IEntityPicker. Spec docs/superpowers/specs/2026-10-01-photo-studio-free-camera-design.md (devkit).
    private const string FreeCamOffEnvVar = "STELLAR_FREECAM_OFF";
    private const string FreezeNoPositionsEnvVar = "STELLAR_FREEZE_NO_POSITIONS";
    private CameraOverrideService? _cameraOverride;
    private InputShieldService? _inputShield;
    private SceneFreezeService? _sceneFreeze;
    private EmoteService? _emotes;
    private CombatStateService? _combatState;
    private EntityPickerService? _entityPicker;
    private PandaCombatFlagSource? _combatFlags;
    private GameFreezeBackend? _freezeBackend;
    private GameClockPause? _clockPause;
    private FrameDriverHost? _frameDriver;
    private FreeCameraReleaser? _freeCamReleaser;
    private SceneLeavePrefix? _sceneLeave;
    // Armed on login / zone change, drained on the first framework tick in a stable world (never a timer).
    private readonly ReassertGate _emoteRefresh = new();
    private TimePauseWatchdog? _pauseWatchdog;
    private Action<bool>? _onClockChanged;
    private Action<float>? _emoteRefreshTick;
    private readonly HashSet<string> _pauseStepWarned = new(StringComparer.Ordinal);

    /// <summary>Constructs the free-camera services in <c>Load()</c> (game types resolve lazily; hooks arm in
    /// <see cref="InstallFreeCameraHooks"/>). Kill switches are read once here.</summary>
    private void WireFreeCamera(BepInExPluginLog log)
    {
        var camOff = Environment.GetEnvironmentVariable(FreeCamOffEnvVar) == "1";
        var noPositions = Environment.GetEnvironmentVariable(FreezeNoPositionsEnvVar) == "1";
        if (camOff) log.Info("[FreeCam] free camera OFF (" + FreeCamOffEnvVar + "=1)");
        if (noPositions) log.Info("[FreeCam] freeze position hold OFF (" + FreezeNoPositionsEnvVar + "=1)");
        Action<string> warn = m => log.Warning("[FreeCam] " + m);
        var entities = new GameEntityAccess(_gameTypeRegistry!);
        _frameDriver = new FrameDriverHost(log);
        var camera = new CinemachineCameraBackend(_gameTypeRegistry!, entities, _frameDriver, log);
        var lookAt = new LookAtService(new LookAtBackend(_gameTypeRegistry!, entities, camera.MainCamera, log), warn);
        _cameraOverride = new CameraOverrideService(camera, lookAt, camOff, warn);
        _inputShield = new InputShieldService(new ZIgnoreShieldBackend(_gameTypeRegistry!, log), new UnityShieldInputReader(), _windowService!, warn);
        _clockPause = new GameClockPause(log);
        _freezeBackend = new GameFreezeBackend(_gameTypeRegistry!, entities, _frameDriver, _clockPause, log);
        _sceneFreeze = new SceneFreezeService(_freezeBackend, positionsDisabled: noPositions, warn);
        _emotes = new EmoteService(_luaService!, warn);
        _combatFlags = new PandaCombatFlagSource(_gameTypeRegistry!, entities, log);
        _combatState = new CombatStateService(_combatService!, _combatService!, _combatFlags, _framework!.Post);
        _entityPicker = new EntityPickerService(entities, camera.MainCamera);
        WirePosing(log, entities, camera.MainCamera);   // Wiring.Posing.cs — needs the freeze + the client state
        WireLights(log, entities);                      // Wiring.Lights.cs — needs posing (visible models, availability)
        _freeCamReleaser = new FreeCameraReleaser(_cameraOverride, _posing!, _sceneFreeze, _inputShield, warn, _lights);
        _sceneLeave = new SceneLeavePrefix(log);
        WireFreeCameraReleases();
        WirePosingSettle();   // Wiring.Posing.cs — AFTER the releases: the release closes every model first (scene end)
        WireFreeCameraKeyboardGate(log);
        WireTimePause(camera, warn);
    }

    /// <summary>The scene freeze's time pause (<c>Time.timeScale = 0</c>; spec amendment 2026-10-02 late): while the clock is
    /// stopped the framework tick runs from real time (the scheduled ticker cannot fire), the camera cuts instead of blending
    /// (a blend never advances), and the local player's movement / combat input is masked (owner report 2026-10-02: WASD while
    /// frozen played the run in place) — the pause block is a layer of the one input shield (qa M-4). Each of the three
    /// steps runs on its own (qa M-9): one that throws is warned once and never skips the others. The watchdog
    /// (<see cref="TimePauseWatchdog"/>) ticks from <see cref="TickTimePauseWatchdog"/> outside the world gate (qa I-1) and
    /// after every paused frame; whatever it finds lost ends in the full release path.</summary>
    private void WireTimePause(CinemachineCameraBackend camera, Action<string> warn)
    {
        var clock = _clockPause!;
        var watchdog = _pauseWatchdog = new TimePauseWatchdog(clock, () => _sceneFreeze!.IsFrozen,
            () => _clientState!.IsWorldActive, ReleaseFreeCamera, warn);
        _onClockChanged = paused =>
        {
            PauseStep("tick driver", warn, () => _tickHost?.SetUnscaled(paused, watchdog.AfterPausedFrame, watchdog.DriverGone));
            PauseStep("camera blend", warn, () => camera.SetCutBlend(paused));
            PauseStep("input block", warn, () => _inputShield!.SetPauseBlock(paused));
        };
        clock.Changed += _onClockChanged;
    }

    private void PauseStep(string step, Action<string> warn, Action run)
    {
        try { run(); }
        catch (Exception ex) { if (_pauseStepWarned.Add(step)) warn("time pause: the " + step + " step threw: " + ex.Message); }
    }

    /// <summary>The pause watchdog at the global rate — called from <c>RunGlobalRateWork</c> beside the login / loading
    /// probes, NOT world-gated (a pause outside the world is exactly what it must see). One field read when not paused.</summary>
    private void TickTimePauseWatchdog()
    {
        try { _pauseWatchdog?.Tick(); }
        catch (Exception ex) { if (_pauseStepWarned.Add("watchdog")) Log.LogWarning("[FreeCam] time-pause watchdog threw: " + ex.Message); }
    }

    /// <summary>Spec D8 (owner 2026-10-01): while any input-shield handle is held, the text-field keyboard gate blocks every
    /// game key. Applied at once on the flip (an Esc in the gap before the next throttled tick would open the game
    /// menu) and re-asserted every tick in <c>TickOverlayServices</c>.</summary>
    private void WireFreeCameraKeyboardGate(BepInExPluginLog log)
    {
        _inputShield!.KeyboardBlockChanged += () =>
        {
            var blocked = _inputShield.KeyboardBlocked;
            _keyboardGate?.SetSuppressed(blocked || (_windowService?.AnyFieldFocused ?? false));
            log.Info("[FreeCam] keyboard gate " + (blocked ? "on (free camera)" : "off"));
        };
    }

    /// <summary>The framework's half of the one release path (spec § 7): zone change, cutscene, the game's camera mode,
    /// disconnect. Plugins see <c>ICameraOverride.Released</c> with the reason and release the rest themselves.
    /// <para>Scene leave releases FIRST from a PREFIX on <c>Game.OnLeaveScene</c> (<see cref="SceneLeavePrefix"/>), before
    /// the game's leave code runs, while every entity of the old scene is still alive — so the camera hand-back, the
    /// freeze's restores and the position hold's release snap (its late driver goes off with it) write only live
    /// models. <c>SceneChanged</c> then fires twice: <c>null</c> from the lifecycle postfix on the same method and the
    /// new name from <c>OnEnterScene</c>; both call the same idempotent release as a backstop (a no-op with nothing
    /// held — <see cref="FreeCameraReleaser"/>), which re-asserts a held shield mask. The combat reseed (a live read
    /// of the local entity) and the emote refresh run on the enter fire only — never mid-teardown.</para></summary>
    private void WireFreeCameraReleases()
    {
        _sceneLeave!.Leaving += () => ReleaseFreeCamera(CameraReleaseReason.SceneChanged);
        _clientState!.SceneChanged += scene =>
        {
            ReleaseFreeCamera(CameraReleaseReason.SceneChanged);   // backstop; no-op after the leave prefix
            if (scene is null) return;
            _combatState!.Reseed();
            _emoteRefresh.Request();
        };
        _clientState.Logout += () => ReleaseFreeCamera(CameraReleaseReason.Disconnected);
        _clientState.Login += _emoteRefresh.Request;
        _photoMode!.Entered += _ => ReleaseFreeCamera(CameraReleaseReason.GamePhotoMode);
        _photoMode.CutsceneChanged += on => { if (on) ReleaseFreeCamera(CameraReleaseReason.Cutscene); };
        _emoteRefreshTick = _ => { if (_emoteRefresh.TryTake(_clientState.IsWorldActive)) _emotes!.Refresh(); };
        _framework!.Update += _emoteRefreshTick;
    }

    private void ReleaseFreeCamera(CameraReleaseReason reason) => _freeCamReleaser?.Release(reason);

    /// <summary>Installs the <c>Game.OnLeaveScene</c> PREFIX (same method + hooker as the lifecycle postfix) once the
    /// game type resolves.</summary>
    private void InstallFreeCameraLeaveHook(HarmonyGameMethodHooker hooker, Type gameType) =>
        _sceneLeave?.Install(hooker, gameType);

    // Arms (does not install): the time-scale / removal hooks and the combat-flag postfixes install on first use.
    private void InstallFreeCameraHooks(HarmonyGameMethodHooker hooker)
    {
        _freezeBackend?.ArmHooks(hooker);
        _combatFlags?.ArmHooks(hooker);
        InstallPosingHooks(hooker);
    }

    private FreeCameraServiceSet FreeCameraSet() =>
        new(_cameraOverride!, _inputShield!, _sceneFreeze!, _emotes!, _combatState!, _entityPicker!, _posing!, _lights!);

    private void DisposeFreeCamera()
    {
        ReleaseFreeCamera(CameraReleaseReason.PluginUnloaded);
        _clockPause?.Dispose();   // never leave the game paused, whatever the release above did
        if (_clockPause is { } clock && _onClockChanged is { } changed) clock.Changed -= changed;   // perf minor: unsubscribe
        if (_framework is { } fw && _emoteRefreshTick is { } tick) fw.Update -= tick;
        _pauseWatchdog = null;
        _frameDriver?.Dispose();
    }
}
