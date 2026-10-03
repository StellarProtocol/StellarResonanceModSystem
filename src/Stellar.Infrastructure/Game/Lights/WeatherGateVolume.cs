using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Game.Lights;

/// <summary>
/// The character-lamp gate (recon Run 13b): <c>CameraManager.Instance.weatherParamsVolume_</c>, a
/// <c>ZBlueProtocolWeatherParamsVolume</c> (normally <c>active = false</c>), its 101 <c>parameterList</c> override flags and
/// its <c>creaturePointlightColorIntensity</c> parameter. Resolved once per volume object (parameter wrappers cached); a
/// later <see cref="Resolve"/> on a different volume object returns a fresh wrapper. Plain reflected property access, no
/// hook. Main thread.
/// </summary>
internal sealed class WeatherGateVolume : IGateVolume
{
    internal const string GateField = "creaturePointlightColorIntensity";

    private readonly Il2CppObjectBase _component;
    private readonly UnityEngine.Object? _unity;
    private readonly object[] _params;
    private readonly PropertyInfo _override;
    private readonly PropertyInfo _value;
    private readonly PropertyInfo _active;
    private readonly object _gate;

    private WeatherGateVolume(Il2CppObjectBase component, object[] parameters, object gate, int gateIndex,
        (PropertyInfo Override, PropertyInfo Value, PropertyInfo Active) members)
    {
        _component = component;
        _unity = component.TryCast<UnityEngine.Object>();
        _params = parameters;
        _gate = gate;
        GateIndex = gateIndex;
        (_override, _value, _active) = members;
    }

    /// <summary>The volume as it is now (null when the game has no camera manager / volume, or a member is missing).</summary>
    public static WeatherGateVolume? Resolve(object? cameraManager)
    {
        var volume = VolumeOf(cameraManager);
        if (volume is not Il2CppObjectBase component || component.WasCollected) return null;
        var vt = volume.GetType();
        var list = StellarInterop.FindPropertyUp(vt, "parameterList")?.GetValue(volume);
        var gate = StellarInterop.FindPropertyUp(vt, GateField)?.GetValue(volume);
        var active = StellarInterop.FindPropertyUp(vt, "active");
        if (list is null || gate is null || active is null) return null;
        var n = StellarInterop.Count(list);
        var items = new object[n];
        var gateIndex = -1;
        var gatePtr = (gate as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
        for (var i = 0; i < n; i++)
        {
            items[i] = StellarInterop.Item(list, i) ?? throw new InvalidOperationException("weather parameter " + i + " is null");
            if (items[i] is Il2CppObjectBase o && o.Pointer == gatePtr) gateIndex = i;
        }
        var overrideState = n > 0 ? StellarInterop.FindPropertyUp(items[0].GetType(), "overrideState") : null;
        var value = StellarInterop.FindPropertyUp(gate.GetType(), "value");
        if (overrideState is null || value is null) return null;
        return new WeatherGateVolume(component, items, gate, gateIndex, (overrideState, value, active));
    }

    /// <summary><c>CameraManager.weatherParamsVolume_</c>, or null.</summary>
    public static object? VolumeOf(object? cameraManager) => cameraManager is null
        ? null
        : StellarInterop.FindPropertyUp(cameraManager.GetType(), "weatherParamsVolume_")?.GetValue(cameraManager);

    /// <summary>The same game object as <paramref name="other"/>.</summary>
    public bool Wraps(object? other) => other is Il2CppObjectBase o && o.Pointer == _component.Pointer;

    public int Count => _params.Length;
    public int GateIndex { get; }
    public bool IsLive => !_component.WasCollected && _unity != null;

    public bool GetOverride(int index) => _override.GetValue(_params[index]) is true;
    public void SetOverride(int index, bool value) => _override.SetValue(_params[index], value);

    public float Value
    {
        get => Convert.ToSingle(_value.GetValue(_gate));
        set => _value.SetValue(_gate, value);
    }

    public bool Active
    {
        get => _active.GetValue(_component) is true;
        set => _active.SetValue(_component, value);
    }
}
