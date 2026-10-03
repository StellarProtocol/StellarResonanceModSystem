using System;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Lights;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Lights (Wiring.Lights.cs) — ILights: cluster lamps, the character-lamp gate, key light / rim per person. Spec
    //    devkit-freecam docs/superpowers/specs/2026-10-03-photo-studio-lights-design.md; recon free-camera-recon.md § Run 13.
    private LightsService? _lights;

    /// <summary>Called from <see cref="WireFreeCamera"/> after <see cref="WirePosing"/>: lights are available exactly when
    /// posing is (in the world, scene settled), light posed copies / stand-ins, and end in the same scene-end release
    /// (<see cref="FreeCameraReleaser"/>).</summary>
    private void WireLights(BepInExPluginLog log, GameEntityAccess entities)
    {
        var posing = _posing!;
        var posingBackend = _posingBackend!;
        var backend = new GameLightsBackend(_gameTypeRegistry!, entities, posingBackend.PosedModel);
        _lights = new LightsService(backend, () => posing.IsAvailable, posing, m => log.Warning("[Lights] " + m), log.Info);
    }
}
