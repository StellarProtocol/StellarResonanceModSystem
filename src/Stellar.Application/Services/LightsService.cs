using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Photo lights (spec devkit-freecam <c>docs/superpowers/specs/2026-10-03-photo-studio-lights-design.md</c>): lamps per
/// owner (at most <see cref="LightLimits.MaxLampsPerPlugin"/> each), the shared character-lamp gate (raised only while a
/// lamp is on and a level is above 0 — <see cref="LightGatePolicy"/>, restored exactly by <see cref="LightGate"/> the moment
/// the last lamp goes off) and a key light / rim per person (People partial). The scene end
/// (<see cref="FreeCameraReleaser"/>: zone change, cutscene, game photo mode, disconnect, framework unload) calls
/// <see cref="ReleaseAll"/>; a plugin's facade calls <see cref="ReleaseOwner"/> on unload. Release order: people first
/// (material write-back), then lamps, then the gate (it follows the last lamp). Each step isolated and warned. Main thread.
/// </summary>
internal sealed partial class LightsService : ILights
{
    private readonly ILightsBackend _backend;
    private readonly Func<bool> _available;
    private readonly Action<string> _warn;
    private readonly Dictionary<int, Lamp> _lamps = new();
    private readonly Dictionary<object, float> _levels = new();
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private LightGate? _gate;
    private int _nextId;

    /// <param name="backend">The game side.</param>
    /// <param name="available">In the world and the scene settled (<c>IPosing.IsAvailable</c>).</param>
    /// <param name="posing">Posing; a person posed or reset gets their key / rim moved to the model now shown.</param>
    /// <param name="warn">Warning sink.</param>
    /// <param name="info">Diagnostics line sink (used only with STELLAR_DIAGNOSTICS on).</param>
    public LightsService(ILightsBackend backend, Func<bool> available, IPosing? posing, Action<string> warn, Action<string> info)
    {
        _backend = backend;
        _available = available;
        _warn = warn;
        _info = info;
        if (posing is not null) posing.Changed += RefreshPeople;
    }

    private sealed class Lamp
    {
        public Lamp(object owner, object handle, LampSettings settings) => (Owner, Handle, Settings) = (owner, handle, settings);
        public object Owner { get; }
        public object Handle { get; }
        public LampSettings Settings { get; set; }
    }

    private static readonly object SharedOwner = new();

    public bool IsAvailable => _available();

    /// <summary>Lamps alive (all owners).</summary>
    internal int LampCount => _lamps.Count;

    /// <summary>The gate is raised now.</summary>
    internal bool GateRaised => _gate is not null;

    /// <summary>The raised gate level, 0 when not raised.</summary>
    internal float GateLevel => _gate?.Level ?? 0f;

    public event Action? Released;

    // The shared service's own members act as one owner (Host-side callers / tests); plugins use PluginLights.
    public LampId AddLamp(LampSettings settings) => AddLamp(SharedOwner, settings);
    public bool UpdateLamp(LampId lamp, LampSettings settings) => UpdateLamp(SharedOwner, lamp, settings);
    public void RemoveLamp(LampId lamp) => RemoveLamp(SharedOwner, lamp);
    public float PeopleLevel { get => GetLevel(SharedOwner); set => SetLevel(SharedOwner, value); }
    public bool SetPersonLight(EntityId person, PersonLight light) => SetPersonLight(SharedOwner, person, light);
    public void ResetAll() => ReleaseOwner(SharedOwner);

    internal LampId AddLamp(object owner, LampSettings settings)
    {
        if (!IsAvailable) return LampId.None;
        if (_lamps.Values.Count(l => ReferenceEquals(l.Owner, owner)) >= LightLimits.MaxLampsPerPlugin) return LampId.None;
        settings = Clamp(settings);
        object? handle;
        try { handle = _backend.CreateLamp(settings); }
        catch (Exception ex)
        {
            WarnOnce("create", "lights: a lamp could not be made: " + (ex.InnerException ?? ex).Message);
            return LampId.None;
        }
        if (handle is null)
        {
            WarnOnce("create-null", "lights: lamps are not available on this client (the game's lamp type was not found)");
            return LampId.None;
        }
        var id = ++_nextId;
        _lamps[id] = new Lamp(owner, handle, settings);
        OnLampsChanged("add", id);
        SyncGate();
        return new LampId(id);
    }

    internal bool UpdateLamp(object owner, LampId lamp, LampSettings settings)
    {
        if (!IsAvailable || !_lamps.TryGetValue(lamp.Value, out var l) || !ReferenceEquals(l.Owner, owner)) return false;
        settings = Clamp(settings);
        try { _backend.UpdateLamp(l.Handle, settings); }
        catch (Exception ex)
        {
            WarnOnce("update", "lights: a lamp could not be changed: " + (ex.InnerException ?? ex).Message);
            return false;
        }
        l.Settings = settings;
        SyncGate();
        return true;
    }

    internal void RemoveLamp(object owner, LampId lamp)
    {
        if (!_lamps.TryGetValue(lamp.Value, out var l) || !ReferenceEquals(l.Owner, owner)) return;
        _lamps.Remove(lamp.Value);
        Step("lamp removal", () => _backend.DestroyLamp(l.Handle));
        OnLampsChanged("remove", lamp.Value);
        SyncGate();
    }

    internal float GetLevel(object owner) => _levels.TryGetValue(owner, out var v) ? v : 0f;

    internal void SetLevel(object owner, float level)
    {
        level = float.IsNaN(level) ? 0f : Math.Clamp(level, 0f, LightLimits.MaxPeopleLevel);
        if (level <= 0f) _levels.Remove(owner);
        else _levels[owner] = level;
        SyncGate();
    }

    /// <summary>One owner's lights go (its <c>ResetAll</c>, or the plugin unloading): people, lamps, then its level.</summary>
    internal void ReleaseOwner(object owner)
    {
        ReleasePeople(p => ReferenceEquals(p.Owner, owner));
        RemoveLamps(l => ReferenceEquals(l.Owner, owner));
        _levels.Remove(owner);
        SyncGate();
    }

    /// <summary>The scene ended (<see cref="FreeCameraReleaser"/>): every person back, every lamp gone, the gate
    /// restored; <see cref="Released"/> once when anything was held. Levels stay (a setting, not a scene object).
    /// Idempotent: with nothing held it does nothing and raises nothing.</summary>
    public void ReleaseAll()
    {
        var held = _people.Count > 0 || _lamps.Count > 0 || _gate is not null;
        if (!held) return;
        ReleasePeople(_ => true);
        RemoveLamps(_ => true);
        Step("gate restore", RestoreGate);
        OnReleasedAll();
        try { Released?.Invoke(); }
        catch (Exception ex) { _warn("lights: a Released handler threw: " + ex.Message); }
    }

    private void RemoveLamps(Func<Lamp, bool> match)
    {
        foreach (var (id, l) in _lamps.Where(kv => match(kv.Value)).ToList())
        {
            _lamps.Remove(id);
            Step("lamp removal", () => _backend.DestroyLamp(l.Handle));
            OnLampsChanged("remove", id);
        }
    }

    /// <summary>Raises, moves or restores the gate to the policy's level. A throw is warned; the gate is then dropped as
    /// restored so a broken volume is never written again.</summary>
    private void SyncGate()
    {
        var owners = _levels.Select(kv => (_lamps.Values.Any(l => ReferenceEquals(l.Owner, kv.Key) && l.Settings.Enabled), kv.Value));
        var level = LightGatePolicy.EffectiveLevel(owners);
        try
        {
            if (level <= 0f) RestoreGate();
            else if (_gate is { } g) { if (g.Level != level) g.SetLevel(level); }
            else RaiseGate(level);
        }
        catch (Exception ex)
        {
            _gate = null;
            WarnOnce("gate", "lights: the character-lamp level could not be changed: " + (ex.InnerException ?? ex).Message);
        }
    }

    private void RaiseGate(float level)
    {
        if (_backend.GateVolume() is not { } volume)
        {
            WarnOnce("gate-none", "lights: the game has no weather volume here; lamps will not light characters");
            return;
        }
        _gate = LightGate.Raise(volume, level);
        if (_gate is null) WarnOnce("gate-param", "lights: the character-lamp level was not found on this client");
        else OnGateRaised(_gate);
    }

    private void RestoreGate()
    {
        if (_gate is not { } g) return;
        _gate = null;
        g.Restore();
        OnGateRestored(g);
    }

    private static LampSettings Clamp(LampSettings s) => s with
    {
        Strength = float.IsNaN(s.Strength) ? 0f : Math.Clamp(s.Strength, 0f, LightLimits.MaxStrength),
        Range = float.IsNaN(s.Range) ? LightLimits.MinRange : Math.Clamp(s.Range, LightLimits.MinRange, LightLimits.MaxRange),
        Color = new RgbColor(Math.Clamp(s.Color.R, 0f, 1f), Math.Clamp(s.Color.G, 0f, 1f), Math.Clamp(s.Color.B, 0f, 1f)),
    };

    private void Step(string what, Action step)
    {
        try { step(); }
        catch (Exception ex) { WarnOnce(what, $"lights: {what} failed: {(ex.InnerException ?? ex).Message}"); }
    }

    partial void OnLampsChanged(string what, int id);
    partial void OnGateRaised(LightGate gate);
    partial void OnGateRestored(LightGate gate);
    partial void OnPersonWritten(long uuid, string what, int saved);
    partial void OnPersonRestored(long uuid, string what, int writes);
    partial void OnReleasedAll();

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _warn(message);
    }
}
