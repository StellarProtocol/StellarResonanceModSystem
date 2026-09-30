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

    partial void OnVolumeCreated();
    partial void OnParamWritten(ParamWrite write, object value);

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
        if (_profileObject != null) UnityEngine.Object.Destroy(_profileObject);
        if (_volumeGo != null) UnityEngine.Object.Destroy(_volumeGo);
        foreach (var tex in _luts.Values)
            if (tex != null) UnityEngine.Object.Destroy(tex);
        _luts.Clear();
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

    /// <summary>Drops every override on every component we added, so a group the new settings omit reverts to the game's value.</summary>
    private void ClearOverrides()
    {
        foreach (var comp in _components.Values)
        {
            var m = StellarInterop.FindMethod(comp.GetType(), "SetAllOverridesTo", 1)
                ?? throw new InvalidOperationException("VolumeComponent.SetAllOverridesTo not found");
            m.Invoke(comp, new object[] { false });
        }
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

    private void WriteParam(ParamWrite w)
    {
        var comp = GetOrAddComponent(w.Component);
        var param = StellarInterop.FindPropertyUp(comp.GetType(), w.Field)?.GetValue(comp);
        if (param is null)
        {
            WarnOnce(w.Component + "." + w.Field, $"Photo look setting {w.Component}.{w.Field} not found; skipped.");
            return;
        }
        var valueProp = StellarInterop.FindPropertyUp(param.GetType(), "value")
            ?? throw new InvalidOperationException($"{w.Field}.value not found");
        var value = ToUnity(w.Value, valueProp.PropertyType);
        if (value is null) return;   // e.g. an unreadable LUT — already warned, leave the game's value
        valueProp.SetValue(param, value);
        SetProp(param, "overrideState", true);
        OnParamWritten(w, value);
    }

    private object? ToUnity(object v, Type target) => v switch
    {
        RgbColor c => new Color(c.R, c.G, c.B, 1f),
        EnumName e => Enum.Parse(target, e.Name),
        LutPath p => LoadLut(p.Path),
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

    private static void SetProp(object target, string name, object value)
    {
        var p = StellarInterop.FindPropertyUp(target.GetType(), name)
            ?? throw new InvalidOperationException($"{target.GetType().Name}.{name} not found");
        p.SetValue(target, value);
    }
}
