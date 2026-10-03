using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Application.Tests.Lights;

/// <summary>Every game write of the lights fakes, in order (one shared log so release ordering can be pinned).</summary>
internal sealed class LightLog
{
    public readonly List<string> Entries = new();
    public void Add(string s) => Entries.Add(s);
    public int IndexOf(string prefix) => Entries.FindIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));
    public int LastIndexOf(string prefix) => Entries.FindLastIndex(e => e.StartsWith(prefix, StringComparison.Ordinal));
}

/// <summary>The weather volume: <see cref="Count"/> override flags, the gate parameter at <see cref="GateIndex"/>.</summary>
internal sealed class FakeGateVolume : IGateVolume
{
    private readonly LightLog _log;
    public readonly bool[] Flags;
    private float _value;
    private bool _active;
    public bool Live = true;

    public FakeGateVolume(LightLog log, int count = 101, int gateIndex = 37)
    {
        _log = log;
        Flags = new bool[count];
        GateIndex = gateIndex;
        // A realistic mixed state: some flags overriding, the volume's own value 1.0 not overriding, inactive.
        for (var i = 0; i < count; i += 3) Flags[i] = true;
        if (gateIndex >= 0 && gateIndex < count) Flags[gateIndex] = false;
        _value = 1f;
        _active = false;
    }

    public int Count => Flags.Length;
    public int GateIndex { get; set; }
    public bool IsLive => Live;
    public bool GetOverride(int index) { _log.Add($"get flag {index}"); return Flags[index]; }
    public void SetOverride(int index, bool value) { _log.Add($"set flag {index}={value}"); Flags[index] = value; }

    public float Value
    {
        get { _log.Add("get value"); return _value; }
        set { _log.Add("set value " + value.ToString("0.###", CultureInfo.InvariantCulture)); _value = value; }
    }

    public bool Active
    {
        get { _log.Add("get active"); return _active; }
        set { _log.Add($"set active {value}"); _active = value; }
    }

    /// <summary>The state without logging (for exact before/after compares).</summary>
    public (bool[] Flags, float Value, bool Active) State => ((bool[])Flags.Clone(), _value, _active);
}

internal sealed class FakeMaterial : IMaterialSlot
{
    private readonly LightLog _log;
    private readonly string _name;
    public readonly Dictionary<LightProperty, LightVector> Values = new();
    public bool Live = true;

    public FakeMaterial(LightLog log, string name, bool hasKey = true, bool hasRim = true)
    {
        _log = log;
        _name = name;
        if (hasKey) Values[LightProperty.CameraLightParm] = new LightVector(0.1f, 0.2f, 0.3f, 0f);
        if (hasRim)
        {
            Values[LightProperty.UseFresnel] = new LightVector(0f, 0f, 0f, 0f);
            Values[LightProperty.FresnelColor] = new LightVector(0.5f, 0.6f, 0.7f, 1f);
            Values[LightProperty.FresnelParms] = new LightVector(-0.3f, 2f, 0.5f, 1f);
        }
    }

    public bool IsLive => Live;
    public bool TryRead(LightProperty property, out LightVector value) => Values.TryGetValue(property, out value);

    public void Write(LightProperty property, LightVector value)
    {
        _log.Add($"write {_name} {property}");
        Values[property] = value;
    }

    public Dictionary<LightProperty, LightVector> Copy() => new(Values);
}

internal sealed class FakeLightModel : ILightModel
{
    private readonly LightLog _log;
    public readonly string Name;
    public readonly FakeMaterial[] Mats;
    public Func<bool> Live = () => true;
    public int MaterialsCalls;

    public FakeLightModel(LightLog log, string name)
    {
        _log = log;
        Name = name;
        // A body material with no rim properties (the Human shader), hair + weapon with both.
        Mats = new[] { new FakeMaterial(log, name + ".body", hasRim: false), new FakeMaterial(log, name + ".hair"), new FakeMaterial(log, name + ".weapon") };
    }

    public bool IsLive => Live();
    public bool IsSame(ILightModel other) => ReferenceEquals(other, this);
    public IMaterialSlot[] Materials() { MaterialsCalls++; return Mats; }

    // The game's calls write every material that carries the property (as SetFixedLight / SetFresnelEffect do).
    public void ApplyKey(LightVector cameraSpace)
    {
        _log.Add($"key {Name}");
        foreach (var m in Mats) if (m.Values.ContainsKey(LightProperty.CameraLightParm)) m.Values[LightProperty.CameraLightParm] = cameraSpace;
    }

    public void ApplyRim(LightVector color, LightVector parms)
    {
        _log.Add($"rim {Name}");
        foreach (var m in Mats)
        {
            if (!m.Values.ContainsKey(LightProperty.UseFresnel)) continue;
            m.Values[LightProperty.UseFresnel] = new LightVector(1f, 0f, 0f, 0f);
            m.Values[LightProperty.FresnelColor] = color;
            m.Values[LightProperty.FresnelParms] = parms;
        }
    }

    public List<Dictionary<LightProperty, LightVector>> Snapshot()
    {
        var list = new List<Dictionary<LightProperty, LightVector>>();
        foreach (var m in Mats) list.Add(m.Copy());
        return list;
    }
}

internal sealed class FakeLightsBackend : ILightsBackend
{
    public readonly LightLog Log = new();
    public readonly FakeGateVolume Volume;
    public readonly Dictionary<long, FakeLightModel> Models = new();
    public readonly List<FakeLamp> Lamps = new();
    public bool NoVolume, ThrowOnCreate;

    public FakeLightsBackend() => Volume = new FakeGateVolume(Log);

    internal sealed class FakeLamp
    {
        public LampSettings Settings;
        public bool Destroyed;
        public int Id;
    }

    public object? CreateLamp(LampSettings settings)
    {
        if (ThrowOnCreate) throw new InvalidOperationException("no lamp");
        var l = new FakeLamp { Settings = settings, Id = Lamps.Count + 1 };
        Lamps.Add(l);
        Log.Add($"create lamp {l.Id}");
        return l;
    }

    public void UpdateLamp(object lamp, LampSettings settings)
    {
        var l = (FakeLamp)lamp;
        l.Settings = settings;
        Log.Add($"update lamp {l.Id}");
    }

    public void DestroyLamp(object lamp)
    {
        var l = (FakeLamp)lamp;
        l.Destroyed = true;
        Log.Add($"destroy lamp {l.Id}");
    }

    public IGateVolume? GateVolume() => NoVolume ? null : Volume;

    public ILightModel? ResolveModel(long uuid) => Models.TryGetValue(uuid, out var m) && m.IsLive ? m : null;

    public FakeLightModel AddPerson(long uuid, string name)
    {
        var m = new FakeLightModel(Log, name);
        Models[uuid] = m;
        return m;
    }
}

/// <summary>A lights service in the world over the fakes.</summary>
internal sealed class LightsRig
{
    public readonly FakeLightsBackend Backend = new();
    public readonly List<string> Warnings = new();
    public readonly LightsService Svc;
    public bool Available = true;

    public LightsRig() => Svc = new LightsService(Backend, () => Available, null, Warnings.Add, _ => { });

    public static LampSettings Lamp(bool on = true, float x = 1f) =>
        new(new Position3D(x, 1f, 2f), new RgbColor(1f, 0.45f, 0.15f), 40f, 6f, on);
}
