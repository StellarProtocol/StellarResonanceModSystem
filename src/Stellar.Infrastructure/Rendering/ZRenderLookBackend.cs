using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Rendering;

// Reflection (Volume / VolumeProfile / VolumeParameter plumbing) lives in ZRenderLookBackend.Reflection.cs;
// StellarDiagnostics-gated write logging in ZRenderLookBackend.Diagnostics.cs.

/// <summary>
/// Owns one global, top-priority <c>UnityEngine.Rendering.Volume</c> with a runtime-created profile and
/// never edits the game's own profiles (docs/recon/photo-studio-render-recon.md). Every game type is
/// reached by name at runtime — CI builds against the refs/ stubs, which carry no render-pipeline or game
/// types. Every miss fails open: one warning, the group is reported unsupported, the game's look stays.
/// Main thread only.
/// </summary>
internal sealed partial class ZRenderLookBackend : ILookBackend, IDisposable
{
    private const string Tag = "[PhotoStudio] ";

    private static readonly LookGroups[] AllGroups =
        { LookGroups.Dof, LookGroups.Color, LookGroups.WhiteBalance, LookGroups.Lut, LookGroups.Bloom, LookGroups.Vignette, LookGroups.FilmGrain };

    private readonly IGameTypeRegistry _types;
    private readonly Func<float?> _focusDistance;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _warnedOnce = new(StringComparer.Ordinal);
    private LookCapabilities? _capabilities;

    public ZRenderLookBackend(IGameTypeRegistry types, Func<float?> focusDistance, IPluginLog log)
    {
        _types = types;
        _focusDistance = focusDistance;
        _log = log;
    }

    /// <summary>Resolved on first read (plugins read it after hot-update load), then cached.</summary>
    public LookCapabilities Capabilities => _capabilities ??= new LookCapabilities(DetectSupported());

    public float? MeasureFocusDistance()
    {
        try { return _focusDistance(); }
        catch { return null; }
    }

    public void Apply(LookSettings? settings)
    {
        try
        {
            if (settings is null) { SetVolumeEnabled(false); return; }
            if (!EnsureVolume()) return;
            ClearOverrides();
            // A rejected LUT drops the whole Lut group (LoadLut warns once and caches the good ones).
            foreach (var w in LookParameterPlan.Build(settings, path => LoadLut(path) is not null)) WriteParam(w);
            SetVolumeEnabled(true);
        }
        catch (Exception ex)
        {
            WarnOnce("apply:" + ex.GetType().Name + ":" + ex.Message, "Photo look could not be applied: " + ex.Message);
            try { SetVolumeEnabled(false); } catch { /* the volume itself is gone — nothing to disable */ }
        }
    }

    /// <summary>Focus tracking: writes only <c>ZDofVolume.FocusDistance</c> on the live look, never a full re-apply.</summary>
    public void UpdateFocus(float distance)
    {
        if (_volume == null || _volumeGo == null || !_components.ContainsKey(LookParameterPlan.DofComponent)) return;
        try { WriteParam(LookParameterPlan.FocusWrite(distance)); }
        catch (Exception ex) { WarnOnce("focus:" + ex.GetType().Name, "Photo look focus could not be updated: " + ex.Message); }
    }

    /// <summary>Destroys our Volume, its runtime profile + components and the cached LUT textures (framework teardown).</summary>
    public void Dispose()
    {
        try { DestroyVolumeObjects(); }
        catch (Exception ex) { _log.Warning(Tag + "Photo look teardown failed: " + ex.Message); }
    }

    private LookGroups DetectSupported()
    {
        if (ResolveType(VolumeTypeName, CoreRuntimeAssembly) is null || ResolveType(ProfileTypeName, CoreRuntimeAssembly) is null)
        {
            _log.Warning(Tag + $"Photo looks unavailable: {VolumeTypeName} / {ProfileTypeName} not found.");
            return LookGroups.None;
        }
        var supported = LookGroups.None;
        foreach (var g in AllGroups)
        {
            var name = LookParameterPlan.ComponentFor(g);
            if (ResolveType(name, ZRenderAssembly) is not null) supported |= g;
            else _log.Warning(Tag + $"Photo look group {g} unavailable: {name} not found.");
        }
        return supported;
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key)) _log.Warning(Tag + message);
    }
}
