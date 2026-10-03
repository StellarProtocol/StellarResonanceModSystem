using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Lights;

/// <summary>
/// One lamp the way recon Run 13 measured it (probe <c>probe/lights@5d31428</c>, <c>R13Lamps</c>): a GameObject made
/// inactive, a Unity point <c>Light</c> (colour, intensity, range, no shadows) and the game's <c>Bokura.Rendering.MultiLight</c>
/// (<c>lightLayer = Everything</c>, <c>type = Common</c>, <c>maxDistance = 128</c>, falloff 4, specular 1, reflection on),
/// kept on a persistent root (<c>DontDestroyOnLoad</c>), then activated — <c>MultiLight.OnEnable</c> adds it to the game's light cluster (<c>MultiLightManager.AddLight</c>) and
/// turns the Unity light itself off. Off = the object inactive (out of the cluster); removal = inactive, then destroyed.
/// Property writes and a <c>MarkDirty()</c> after each change — plain reflected calls, no hook. Main thread.
/// </summary>
internal sealed class LampCalls
{
    internal const string MultiLightType = "Bokura.Rendering.MultiLight";
    internal const string LayerType = "Bokura.Rendering.MultiLightLayer";
    private const string RenderAssembly = "ZRenderPipeline";

    private readonly IGameTypeRegistry _types;
    private Type? _ml;
    private Il2CppSystem.Type? _mlIl2Cpp;
    private PropertyInfo? _layer, _kind, _maxDistance, _falloff, _specular, _reflection;
    private MethodInfo? _markDirty;
    private object? _everything, _common;

    public LampCalls(IGameTypeRegistry types) => _types = types;

    /// <summary>A made lamp: the object, its Unity light and the game's cluster component.</summary>
    internal sealed record Lamp(GameObject Go, Light Light, object Multi);

    /// <summary>Null when the game's lamp types are not there (warned by the caller).</summary>
    public Lamp? Create(LampSettings s)
    {
        if (!Resolve()) return null;
        var go = new GameObject("StellarPhotoLamp");
        try
        {
            // A persistent root (review): never part of the active scene / a streamed chunk, whose unload would destroy the
            // lamp under us. The framework itself removes every lamp on a scene end (ILights.Released).
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.SetActive(false);
            var light = go.AddComponent(Il2CppType.Of<Light>()).Cast<Light>();
            light.type = LightType.Point;
            light.shadows = LightShadows.None;
            var comp = go.AddComponent(_mlIl2Cpp!);
            var ml = Activator.CreateInstance(_ml!, comp.Pointer)!;
            _layer!.SetValue(ml, _everything);
            _kind!.SetValue(ml, _common);
            _maxDistance!.SetValue(ml, 128f);
            _falloff?.SetValue(ml, 4f);
            _specular?.SetValue(ml, 1f);
            _reflection?.SetValue(ml, true);
            var lamp = new Lamp(go, light, ml);
            Apply(lamp, s);
            return lamp;
        }
        catch
        {
            UnityEngine.Object.Destroy(go);
            throw;
        }
    }

    /// <summary>Position, colour, intensity, range, then on/off; a destroyed lamp is a no-op.</summary>
    public void Apply(Lamp lamp, LampSettings s)
    {
        if (lamp.Go == null || lamp.Light == null) return;
        lamp.Go.transform.position = new Vector3(s.Position.X, s.Position.Y, s.Position.Z);
        lamp.Light.color = new Color(s.Color.R, s.Color.G, s.Color.B, 1f);
        lamp.Light.intensity = s.Strength;
        lamp.Light.range = s.Range;
        if (lamp.Go.activeSelf != s.Enabled) lamp.Go.SetActive(s.Enabled);
        if (s.Enabled) _markDirty?.Invoke(lamp.Multi, null);
    }

    /// <summary>Out of the cluster now (inactive → <c>OnDisable</c>), destroyed at the end of the frame.</summary>
    public static void Destroy(Lamp lamp)
    {
        if (lamp.Go == null) return;
        lamp.Go.SetActive(false);
        UnityEngine.Object.Destroy(lamp.Go);
    }

    private bool Resolve()
    {
        if (_mlIl2Cpp is not null) return true;
        var ml = Find(MultiLightType);
        var layer = Find(LayerType);
        var kind = ml?.GetNestedType("LightType");
        if (ml is null || layer is null || kind is null) return false;
        _layer = StellarInterop.FindPropertyUp(ml, "lightLayer");
        _kind = StellarInterop.FindPropertyUp(ml, "type");
        _maxDistance = StellarInterop.FindPropertyUp(ml, "maxDistance");
        _falloff = StellarInterop.FindPropertyUp(ml, "falloffExponent");
        _specular = StellarInterop.FindPropertyUp(ml, "specularScale");
        _reflection = StellarInterop.FindPropertyUp(ml, "IsReflection");
        _markDirty = StellarInterop.FindMethod(ml, "MarkDirty", 0);
        if (_layer is null || _kind is null || _maxDistance is null) return false;
        _everything = Enum.Parse(layer, "Everything");
        _common = Enum.Parse(kind, "Common");
        _ml = ml;
        _mlIl2Cpp = Il2CppType.From(ml);
        return true;
    }

    private Type? Find(string fullName)
    {
        try { return _types.FindType(fullName) ?? Type.GetType(fullName + ", " + RenderAssembly, throwOnError: false); }
        catch { return null; }
    }
}
