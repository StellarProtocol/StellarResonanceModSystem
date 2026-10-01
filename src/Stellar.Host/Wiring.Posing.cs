using System;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Posing;
using Stellar.Infrastructure.Hooks;
using UnityEngine;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Posing (Wiring.Posing.cs) — IPosing on top of the free camera. Spec docs/superpowers/specs/
    //    2026-10-02-photo-studio-posing-design.md (devkit); recon docs/recon/photo-posing-recon.md.
    private PosingService? _posing;
    private GamePosingBackend? _posingBackend;
    private PosingSendTap? _posingSendTap;

    /// <summary>Called from <see cref="WireFreeCamera"/> once the camera arbiter and the freeze exist: posing lives and
    /// dies with the free camera (every <c>Released</c> resets every touched person).</summary>
    private void WirePosing(BepInExPluginLog log, GameEntityAccess entities, Func<Camera?> mainCamera)
    {
        Action<string> warn = m => log.Warning("[Posing] " + m);
        var mainThread = Environment.CurrentManagedThreadId;   // Load() runs on Unity's main thread
        Action<Action> onMain = a => { if (Environment.CurrentManagedThreadId == mainThread) a(); else _framework!.Post(a); };
        var calls = new PoseCalls(new PoseActionCalls(_gameTypeRegistry!), new PoseModelCalls(_gameTypeRegistry!),
            new PoseSpawnCalls(_gameTypeRegistry!, warn), new LookAtSnapshotReader(_gameTypeRegistry!), new PoseLuaQueries(_luaService!),
            mainCamera, warn, onMain);
        _posingBackend = new GamePosingBackend(calls, entities, _gameTypeRegistry!, log);
        _posing = new PosingService(_posingBackend, _cameraOverride!, _sceneFreeze!, warn);
        var posing = _posing;
        _posingSendTap = new PosingSendTap(_gameTypeRegistry!, () => posing.HasTargets, log);
    }

    /// <summary>Opens the ~2 s scene-change settle window on a scene leave (the <c>Game.OnLeaveScene</c> prefix) and on
    /// the enter (<c>IClientState.SceneChanged</c>). Subscribed AFTER <see cref="WireFreeCameraReleases"/> on the same
    /// events, so the free-camera release (which resets every posed person) runs first, then the window arms.</summary>
    private void WirePosingSettle()
    {
        if (_posingBackend is not { } backend) return;
        _sceneLeave!.Leaving += backend.SceneChanged;
        _clientState!.SceneChanged += _ => backend.SceneChanged();
    }

    /// <summary>Arms the despawn prefix (installed on the first open) and, with diagnostics on, the send tap.</summary>
    private void InstallPosingHooks(HarmonyGameMethodHooker hooker)
    {
        _posingBackend?.ArmHooks(hooker);
        _posingSendTap?.Install(hooker);
    }
}
