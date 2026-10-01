using System;
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
    private FrameDriverHost? _frameDriver;
    // Armed on login / zone change, drained on the first framework tick in a stable world (never a timer).
    private readonly ReassertGate _emoteRefresh = new();

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
        _inputShield = new InputShieldService(new ZIgnoreShieldBackend(_gameTypeRegistry!, log), new UnityShieldInputReader(), warn);
        _freezeBackend = new GameFreezeBackend(_gameTypeRegistry!, entities, _frameDriver, log);
        _sceneFreeze = new SceneFreezeService(_freezeBackend, positionsDisabled: noPositions);
        _emotes = new EmoteService(_luaService!, warn);
        _combatFlags = new PandaCombatFlagSource(_gameTypeRegistry!, entities, log);
        _combatState = new CombatStateService(_combatService!, _combatService!, _combatFlags, _framework!.Post);
        _entityPicker = new EntityPickerService(entities, camera.MainCamera);
        WireFreeCameraReleases();
        WireFreeCameraKeyboardGate(log);
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
    /// <para><c>SceneChanged</c> fires twice per switch: with <c>null</c> from the <c>Game.OnLeaveScene</c> postfix
    /// (Wiring.Wire.cs — the earliest scene-leave signal the framework has, the same one that gates the tick) and with
    /// the new name from <c>OnEnterScene</c>. The leave fire is the one that releases, so the camera, the position
    /// hold (its late driver goes off with it) and the freeze are handed back before the old scene's entities are torn
    /// down; the enter fire finds nothing to release and only re-asserts a held shield mask. The combat reseed is a
    /// live read of the local entity, so it runs on the enter fire only — never mid-teardown.</para></summary>
    private void WireFreeCameraReleases()
    {
        _clientState!.SceneChanged += scene =>
        {
            ReleaseFreeCamera(CameraReleaseReason.SceneChanged);
            if (scene is not null) _combatState!.Reseed();
            _emoteRefresh.Request();
        };
        _clientState.Logout += () => ReleaseFreeCamera(CameraReleaseReason.Disconnected);
        _clientState.Login += _emoteRefresh.Request;
        _photoMode!.Entered += _ => ReleaseFreeCamera(CameraReleaseReason.GamePhotoMode);
        _photoMode.CutsceneChanged += on => { if (on) ReleaseFreeCamera(CameraReleaseReason.Cutscene); };
        _framework!.Update += _ => { if (_emoteRefresh.TryTake(_clientState.IsWorldActive)) _emotes!.Refresh(); };
    }

    private void ReleaseFreeCamera(CameraReleaseReason reason)
    {
        _cameraOverride?.ReleaseAll(reason);
        _sceneFreeze?.ReleaseAll();
        if (reason == CameraReleaseReason.SceneChanged) _inputShield?.Reassert();
        else _inputShield?.ReleaseAll();
    }

    // Arms (does not install): the effect-creation and combat-flag postfixes install on first use.
    private void InstallFreeCameraHooks(HarmonyGameMethodHooker hooker)
    {
        _freezeBackend?.ArmHooks(hooker);
        _combatFlags?.ArmHooks(hooker);
    }

    private FreeCameraServiceSet FreeCameraSet() =>
        new(_cameraOverride!, _inputShield!, _sceneFreeze!, _emotes!, _combatState!, _entityPicker!);

    private void DisposeFreeCamera()
    {
        ReleaseFreeCamera(CameraReleaseReason.PluginUnloaded);
        _frameDriver?.Dispose();
    }
}
