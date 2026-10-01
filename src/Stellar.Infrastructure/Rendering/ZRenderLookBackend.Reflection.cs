using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Volume plumbing, ported from the Task 0 probe (StellarPhotoStudioPlugin branch <c>probe/render-recon</c>,
/// <c>RenderProbe.Look.cs</c>): <c>AddComponent(Il2CppType.From(Volume))</c>, isGlobal / priority 10000 /
/// weight 1, a runtime <c>VolumeProfile</c>, <c>profile.Add(Il2CppType.From(t), false)</c> wrapped as
/// <c>Activator.CreateInstance(t, ptr)</c>, and <c>param.value</c> + <c>param.overrideState</c> per write.
/// </summary>
internal sealed partial class ZRenderLookBackend
{
    private const string VolumeTypeName = "UnityEngine.Rendering.Volume";
    private const string ProfileTypeName = "UnityEngine.Rendering.VolumeProfile";
    private const string CoreRuntimeAssembly = "Unity.RenderPipelines.Core.Runtime";
    private const string ZRenderAssembly = "ZRenderPipeline";
    // Beats the game's camera volume (10), cutscene volume (0) and local scene volumes (1-5). Recon.
    private const float VolumePriority = 10000f;

    private GameObject? _volumeGo;
    private Behaviour? _volume;
    private object? _profile;
    private ScriptableObject? _profileObject;
    private MethodInfo? _profileAdd;
    private readonly Dictionary<string, object> _components = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _luts = new(StringComparer.Ordinal);
    private Texture2D? _grainTexture;

    partial void OnVolumeCreated();
    partial void OnParamWritten(ParamWrite write, object value);
    partial void OnFocusWritten(float distance);
    partial void OnGrainTextureCreated(int size, byte[] pixels);

    private Type? ResolveType(string fullName, string assembly)
    {
        try { return _types.FindType(fullName) ?? Type.GetType(fullName + ", " + assembly, throwOnError: false); }
        catch { return null; }
    }

    /// <summary>Creates our Volume + profile once (and again if something destroyed the object).</summary>
    private bool EnsureVolume()
    {
        if (_volume != null && _volumeGo != null) return true;
        DestroyVolumeObjects();   // something destroyed our volume: drop the orphaned profile / components / LUTs first
        var volType = ResolveType(VolumeTypeName, CoreRuntimeAssembly);
        var profileType = ResolveType(ProfileTypeName, CoreRuntimeAssembly);
        if (volType is null || profileType is null)
        {
            WarnOnce("volume-type", "Photo looks unavailable: the renderer's Volume type was not found.");
            return false;
        }
        // Layer left at 0: the game's own camera volume is on layer 0 (recon), so our volume is in its mask.
        var go = new GameObject("StellarPhotoLook");
        try
        {
            UnityEngine.Object.DontDestroyOnLoad(go);
            var comp = go.AddComponent(Il2CppType.From(volType));
            var volume = Activator.CreateInstance(volType, comp.Pointer)!;
            SetProp(volume, "isGlobal", true);
            SetProp(volume, "priority", VolumePriority);
            SetProp(volume, "weight", 1f);
            var so = ScriptableObject.CreateInstance(Il2CppType.From(profileType));
            so.hideFlags = HideFlags.DontUnloadUnusedAsset;
            _profileObject = so;
            _profile = Activator.CreateInstance(profileType, so.Pointer)!;
            SetProp(volume, "sharedProfile", _profile);
            _profileAdd = profileType.GetMethod("Add", new[] { typeof(Il2CppSystem.Type), typeof(bool) })
                ?? throw new InvalidOperationException("VolumeProfile.Add(Type, bool) not found");
            _volume = comp.Cast<Behaviour>();
            _volumeGo = go;
        }
        catch
        {
            UnityEngine.Object.Destroy(go);
            _volume = null;
            _volumeGo = null;
            throw;
        }
        OnVolumeCreated();
        return true;
    }

    private void DestroyVolumeObjects()
    {
        foreach (var comp in _components.Values)
            if (comp is UnityEngine.Object o && o != null) UnityEngine.Object.Destroy(o);
        _components.Clear();
        _params.Clear();          // param wrappers belong to the destroyed components
        _appliedValid = false;    // a fresh volume starts with nothing applied
        if (_profileObject != null) UnityEngine.Object.Destroy(_profileObject);
        if (_volumeGo != null) UnityEngine.Object.Destroy(_volumeGo);
        foreach (var tex in _luts.Values)
            if (tex != null) UnityEngine.Object.Destroy(tex);
        _luts.Clear();
        if (_grainTexture != null) UnityEngine.Object.Destroy(_grainTexture);
        _grainTexture = null;
        _profileObject = null;
        _profile = null;
        _profileAdd = null;
        _volume = null;
        _volumeGo = null;
    }

    private void SetVolumeEnabled(bool on)
    {
        if (_volume != null) _volume.enabled = on;
    }

    private object GetOrAddComponent(string typeName)
    {
        if (_components.TryGetValue(typeName, out var cached)) return cached;
        var t = ResolveType(typeName, ZRenderAssembly) ?? throw new InvalidOperationException(typeName + " not found");
        var ret = (Il2CppObjectBase)_profileAdd!.Invoke(_profile, new object[] { Il2CppType.From(t), false })!;
        var typed = Activator.CreateInstance(t, ret.Pointer)!;
        SetProp(typed, "active", true);
        _components[typeName] = typed;
        return typed;
    }

    private object? ToUnity(object v, Type target) => v switch
    {
        RgbColor c => new Color(c.R, c.G, c.B, 1f),
        EnumName e => Enum.Parse(target, e.Name),
        LutPath p => LoadLut(p.Path),
        GeneratedNoiseTexture n => GrainTexture(n.Size),
        Vec2 v2 => new Vector2(v2.X, v2.Y),
        _ => v,
    };

    /// <summary>Loads a 256×16 or 1024×32 PNG strip as a linear, clamped texture; cached by path.</summary>
    private Texture2D? LoadLut(string path)
    {
        if (_luts.TryGetValue(path, out var cached) && cached != null) return cached;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            WarnOnce("lut-read:" + path, $"LUT {Path.GetFileName(path)} could not be read: {ex.Message}");
            return null;
        }
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        if (!ImageConversion.LoadImage(tex, bytes) || !LookParameterPlan.IsLutSize(tex.width, tex.height))
        {
            WarnOnce("lut-size:" + path, $"LUT {Path.GetFileName(path)} is not a 256x16 or 1024x32 PNG strip; skipped.");
            UnityEngine.Object.Destroy(tex);
            return null;
        }
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        tex.name = "StellarPhotoLut";
        _luts[path] = tex;
        return tex;
    }

    /// <summary>The generated grain texture (linear, Repeat-wrapped, never mip-mapped); created once, destroyed with
    /// the volume objects.</summary>
    private Texture2D GrainTexture(int size)
    {
        if (_grainTexture != null) return _grainTexture;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
        var pixels = GrainNoise.Generate(size);
        tex.LoadRawTextureData(pixels);
        tex.wrapMode = TextureWrapMode.Repeat;
        tex.filterMode = FilterMode.Bilinear;
        tex.hideFlags = HideFlags.DontUnloadUnusedAsset;
        tex.name = "StellarPhotoGrain";
        tex.Apply(false, true);   // upload, then drop the CPU copy
        _grainTexture = tex;
        OnGrainTextureCreated(size, pixels);
        return tex;
    }

    private void SetProp(object target, string name, object value)
    {
        var p = FindProp(target.GetType(), name)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} not found");
        p.SetValue(target, value);
    }
}
