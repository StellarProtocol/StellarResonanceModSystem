using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Rendering;

// Reflection resolution: ZRenderQualityBackend.Reflection.cs. Game hooks: ZRenderQualityBackend.Hooks.cs.
// StellarDiagnostics-gated per-write logging: ZRenderQualityBackend.Diagnostics.cs.

/// <summary>
/// The game side of <c>IRenderQuality</c>, ported from MahiruUtility's <c>RenderQualityControl</c> (proven on the
/// owner's client) — Unity's own <c>QualitySettings</c> is ignored by the game's custom SRP, so every lever is the
/// game's own object, reached by reflection:
/// <list type="bullet">
/// <item>render scale — <c>Bokura.Rendering.ZRenderPipeline.asset.renderScale</c> (the game's 50–150 % slider drives it);</item>
/// <item>TAA — <c>QualityGradeSetting.EnableAA</c> AND the private <c>applyEnableAA(bool)</c> (the property alone is not pushed);</item>
/// <item>shadows — <c>ZShadowCastPass</c> singleton → <c>shadowSettings</c> → <c>shadowmapResolution</c> /
/// <c>cascadesCount</c> / <c>supportsSoftShadows</c> (each a field or a property; the object is re-read every call,
/// so a re-initialised pass is never written through a stale reference).</item>
/// </list>
/// Reads return null outside a stable world scene (<see cref="IClientState.IsWorldActive"/>). Writes never throw
/// (one warning per failure kind). Never touches <c>ZServerTime</c>. Main thread only. Recon:
/// devkit docs/recon/photo-studio-render-recon.md § Render quality + time of day recon.
/// </summary>
internal sealed partial class ZRenderQualityBackend : IRenderQualityBackend
{
    private const string Tag = "[PhotoQuality] ";

    private readonly IGameTypeRegistry _types;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private bool _writing;   // true while WE write, so the game-apply hooks ignore our own calls

    public ZRenderQualityBackend(IGameTypeRegistry types, IClientState clientState, IPluginLog log)
    {
        _types = types;
        _clientState = clientState;
        _log = log;
    }

    /// <summary>Raised (main thread) after one of the game's quality-apply entry points ran — never for our own writes.</summary>
    public event Action? GameApplied;

    public RenderQualityCapabilities Capabilities => new(ResolveScale(), ResolveTaa(), ResolveShadows());

    public float? ReadRenderScale()
    {
        if (!_clientState.IsWorldActive || !ResolveScale()) return null;
        try { return _pipelineAsset!.GetValue(null) is { } asset ? Convert.ToSingle(_renderScale!.GetValue(asset)) : null; }
        catch { return null; }
    }

    public bool WriteRenderScale(float scale) => Write("scale", scale, () =>
    {
        var asset = _pipelineAsset!.GetValue(null) ?? throw new InvalidOperationException("pipeline asset is null");
        _renderScale!.SetValue(asset, scale);
    });

    public bool? ReadTaa()
    {
        if (!_clientState.IsWorldActive || !ResolveTaa()) return null;
        try { return Convert.ToBoolean(_enableAA!.GetValue(null)); }
        catch { return null; }
    }

    public bool WriteTaa(bool on) => Write("taa", on, () =>
    {
        _enableAA!.SetValue(null, on);
        _applyEnableAA!.Invoke(null, new object[] { on });
    });

    public ShadowValues? ReadShadows()
    {
        if (!_clientState.IsWorldActive || !ResolveShadows()) return null;
        try
        {
            return LiveShadowSettings() is { } s
                ? new ShadowValues(Convert.ToInt32(_shadowRes.Get(s)), Convert.ToInt32(_cascades.Get(s)), Convert.ToBoolean(_soft.Get(s)))
                : null;
        }
        catch { return null; }
    }

    public bool WriteShadows(ShadowValues values) => Write("shadows", values, () =>
    {
        var s = LiveShadowSettings() ?? throw new InvalidOperationException("shadow settings are null");
        // Per member, only when it differs: a shadow-map resolution write reallocates the shadow atlas.
        if (Convert.ToInt32(_shadowRes.Get(s)) != values.Resolution) _shadowRes.Set(s, values.Resolution);
        if (Convert.ToInt32(_cascades.Get(s)) != values.Cascades) _cascades.Set(s, values.Cascades);
        if (Convert.ToBoolean(_soft.Get(s)) != values.Soft) _soft.Set(s, values.Soft);
    });

    private bool Write(string lever, object value, Action write)
    {
        _writing = true;
        try
        {
            write();
            OnLeverWritten(lever, value);
            return true;
        }
        catch (Exception ex)
        {
            WarnOnce("write:" + lever, $"Could not set {lever}: {(ex.InnerException ?? ex).Message}");
            return false;
        }
        finally
        {
            _writing = false;
        }
    }

    private void OnGameApply(string method)
    {
        if (_writing) return;
        OnGameApplyObserved(method);
        GameApplied?.Invoke();
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnLeverWritten(string lever, object value);
    partial void OnGameApplyObserved(string method);
}
