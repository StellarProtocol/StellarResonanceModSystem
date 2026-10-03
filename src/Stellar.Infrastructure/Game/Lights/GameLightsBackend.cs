using System;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Posing;
namespace Stellar.Infrastructure.Game.Lights;

/// <summary>
/// Game side of <c>ILights</c> (devkit recon <c>docs/recon/free-camera-recon.md</c> § Run 13; probe
/// <c>probe/lights@5d31428</c>): cluster lamps (<see cref="LampCalls"/>), the weather volume that gates lamp light on
/// characters (<see cref="WeatherGateVolume"/>, through <c>CameraManager.Instance.weatherParamsVolume_</c>) and a person's
/// visible model — the posed copy / NPC stand-in from <see cref="GamePosingBackend.PosedModel"/> while there is one, else
/// the entity's live model (<see cref="LightModelCalls"/>). Plain reflected calls only; nothing is hooked. Main thread.
/// </summary>
internal sealed class GameLightsBackend : ILightsBackend
{
    private const string CameraManagerType = "Panda.ZGame.CameraManager";

    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly Func<long, object?> _posedModel;
    private readonly LampCalls _lamps;
    private readonly LightModelCalls _models;
    private readonly SingletonAccess _cameraManager = new();
    private WeatherGateVolume? _volume;

    /// <param name="types">Game type lookup.</param>
    /// <param name="entities">Entity → live model.</param>
    /// <param name="posedModel">The posed copy / stand-in of a person (<see cref="GamePosingBackend.PosedModel"/>).</param>
    public GameLightsBackend(IGameTypeRegistry types, GameEntityAccess entities, Func<long, object?> posedModel)
    {
        _types = types;
        _entities = entities;
        _posedModel = posedModel;
        _lamps = new LampCalls(types);
        _models = new LightModelCalls(types);
    }

    public object? CreateLamp(LampSettings settings) => _lamps.Create(settings);

    public void UpdateLamp(object lamp, LampSettings settings)
    {
        if (lamp is LampCalls.Lamp l) _lamps.Apply(l, settings);
    }

    public void DestroyLamp(object lamp)
    {
        if (lamp is LampCalls.Lamp l) LampCalls.Destroy(l);
    }

    /// <summary>The volume now; the wrapper is reused while the game keeps the same volume object.</summary>
    public IGateVolume? GateVolume()
    {
        var t = _types.FindType(CameraManagerType);
        if (t is null || !_cameraManager.Resolve(t) || _cameraManager.Get() is not { } mgr) return null;
        if (_volume is { IsLive: true } current && current.Wraps(WeatherGateVolume.VolumeOf(mgr))) return current;
        _volume = WeatherGateVolume.Resolve(mgr);
        return _volume;
    }

    public ILightModel? ResolveModel(long uuid)
    {
        var model = _posedModel(uuid);
        if (model is null || !_models.IsLive(model)) model = _entities.LiveModel(_entities.EntityByUuid(uuid));
        return model is Il2CppObjectBase m && _models.IsLive(m) ? new GameLightModel(_models, m) : null;
    }
}
