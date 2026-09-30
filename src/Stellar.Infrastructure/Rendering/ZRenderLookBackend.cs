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
    // The writes currently live on our components (the diff baseline). Only meaningful while _appliedValid;
    // false (fresh volume, a released look, a failed apply) makes the next Apply clear every override first.
    private readonly List<ParamWrite> _applied = new();
    private bool _appliedValid;
    private bool _dofLive;             // the applied look carries the Dof group (the only time focus may be written)
    private float? _focusSinceApply;   // focus written by UpdateFocus after the last Apply (patched into _applied)

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
            if (settings is null) { Release(); return; }
            if (!EnsureVolume()) return;
            // A rejected LUT drops the whole Lut group (LoadLut warns once and caches the good ones).
            var next = LookParameterPlan.Build(settings, path => LoadLut(path) is not null);
            if (!_appliedValid) { ClearAllOverrides(); _applied.Clear(); _focusSinceApply = null; }
            PatchTrackedFocus();
            // Write only what changed; clear only a group that turned off (perf review: no full re-apply per update).
            var diff = LookParameterPlan.Diff(_applied, next);
            foreach (var c in diff.ClearComponents) ClearOverrides(c);
            foreach (var w in diff.Writes) WriteParam(w);
            _applied.Clear();
            _applied.AddRange(next);
            _dofLive = _applied.Exists(w => w.Component == LookParameterPlan.DofComponent);
            _appliedValid = true;
            SetVolumeEnabled(true);
        }
        catch (Exception ex)
        {
            _appliedValid = false;
            WarnOnce("apply:" + ex.GetType().Name + ":" + ex.Message, "Photo look could not be applied: " + ex.Message);
            try { SetVolumeEnabled(false); } catch { /* the volume itself is gone — nothing to disable */ }
        }
    }

    /// <summary>Focus tracking: writes only <c>ZDofVolume.FocusDistance</c> on the live look, never a full re-apply.
    /// Allocation-free after the first call (cached parameter slot + setter delegates).</summary>
    public void UpdateFocus(float distance)
    {
        if (_volume == null || _volumeGo == null || !_appliedValid || !_dofLive) return;
        try
        {
            WriteFocus(distance);
            _focusSinceApply = distance;
        }
        catch (Exception ex) { WarnOnce("focus:" + ex.GetType().Name, "Photo look focus could not be updated: " + ex.Message); }
    }

    // Our volume stays (disabled) with its components; the next Apply starts from a clean slate.
    private void Release()
    {
        SetVolumeEnabled(false);
        _appliedValid = false;
    }

    // The focus path wrote FocusDistance behind the diff baseline's back: make the baseline say so, or the next
    // Apply would skip a FocusDistance that equals the stale baseline value while the component holds another.
    private void PatchTrackedFocus()
    {
        if (_focusSinceApply is not float f) return;
        _focusSinceApply = null;
        var i = _applied.FindIndex(w => w.Component == LookParameterPlan.DofComponent && w.Field == LookParameterPlan.FocusField);
        if (i >= 0) _applied[i] = LookParameterPlan.FocusWrite(f);
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
