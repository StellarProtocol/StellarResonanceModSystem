using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Union arbiter for <see cref="IRenderQuality"/> (spec 2026-10-01 photo-studio-render-quality § 3). Every held
/// token contributes its levers; each lever is overridden while ANY token asks for it and restored when the last
/// one goes. Per-lever capture/restore lives in <see cref="LeverOverride{T}"/>; every write happens only when the
/// live value differs (a render-scale or shadow-map write reallocates render targets). <see cref="Reassert"/> is
/// driven by game events (scene change, the game's quality-apply hooks) — never by a timer. Main thread only.
/// </summary>
internal sealed class RenderQualityService : IRenderQuality
{
    internal const float SupersampleScale = 2.0f;   // the pipeline's k_MaxRenderScale; above it screen-space AO washes out
    internal const float CaptureScale = 1.0f;       // the capture guard's scale while a grab renders N× itself
    internal const int HighShadowResolution = 4096; // the game's highest preset is 2048
    internal const int HighShadowCascades = 3;      // ZShadowCastPass.k_MaxCascades
    private const float ScaleEpsilon = 0.01f;

    private readonly IRenderQualityBackend _backend;
    private readonly List<Token> _tokens = new();
    private readonly LeverOverride<float> _scale = new((a, b) => Math.Abs(a - b) <= ScaleEpsilon);
    private readonly LeverOverride<bool> _taa = new((a, b) => a == b);
    private readonly LeverOverride<int> _shadowRes = new((a, b) => a == b);
    private readonly LeverOverride<int> _cascades = new((a, b) => a == b);
    private readonly LeverOverride<bool> _soft = new((a, b) => a == b);
    private readonly Action<string> _warn;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private ShadowValues _shadowLive;   // live shadow values while one ApplyShadows writes member by member
    private int _captureSuspends;

    public RenderQualityService(IRenderQualityBackend backend, Action<string>? warn = null)
    {
        _backend = backend;
        _warn = warn ?? (_ => { });
    }

    public RenderQualityCapabilities Capabilities => _backend.Capabilities;

    public RenderQualityState Live
    {
        get
        {
            var shadows = _backend.ReadShadows();
            return new RenderQualityState(_backend.ReadRenderScale() ?? 0f, _backend.ReadTaa() ?? false,
                shadows?.Resolution ?? 0, shadows?.Cascades ?? 0, shadows?.Soft ?? false);
        }
    }

    public IDisposable Request(RenderQualityRequest request) => Request(request, owner: null);

    internal IDisposable Request(RenderQualityRequest request, object? owner)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        _backend.EnsureHooks();   // game hooks install on first use, never at boot
        var token = new Token(this, request.Supersample, request.HighShadows, owner);
        _tokens.Add(token);
        Apply();
        return token;
    }

    /// <summary>Releases every token <paramref name="owner"/> holds (plugin unload).</summary>
    internal void ReleaseOwner(object owner)
    {
        foreach (var t in _tokens.Where(t => Equals(t.Owner, owner)).ToList()) t.Dispose();
    }

    /// <summary>
    /// Capture guard (spec § 4): while held, a supersampled render scale drops to <see cref="CaptureScale"/> so an
    /// N× capture is not rendered at N×2. No effect unless a token holds supersampling. Dispose restores.
    /// </summary>
    internal IDisposable SuspendSupersampleForCapture()
    {
        _captureSuspends++;
        Apply();
        return new Suspension(this);
    }

    /// <summary>Re-reads every lever and re-writes the ones that drifted (the game re-applied its quality grade),
    /// and retries a restore a previous release could not write. No-op when nothing is held or pending.</summary>
    internal void Reassert() => Apply();

    private void Apply()
    {
        var supersample = _tokens.Any(t => t.Supersample);
        var highShadows = _tokens.Any(t => t.HighShadows);
        float? scaleTarget = supersample ? (_captureSuspends > 0 ? CaptureScale : SupersampleScale) : null;
        if (supersample || _scale.Holding) ApplyScale(scaleTarget);
        if (supersample || _taa.Holding) ApplyTaa(supersample ? true : null);
        if (highShadows || _shadowRes.Holding || _cascades.Holding || _soft.Holding) ApplyShadows(highShadows);
    }

    private void ApplyScale(float? target)
    {
        if (_backend.ReadRenderScale() is not float cur) return;   // unreadable now — the next re-assert retries
        Note("render scale", _scale.Step(cur, target, v => (_backend.WriteRenderScale(v), _backend.ReadRenderScale())));
    }

    private void ApplyTaa(bool? target)
    {
        if (_backend.ReadTaa() is not bool cur) return;
        Note("TAA", _taa.Step(cur, target, v => (_backend.WriteTaa(v), _backend.ReadTaa())));
    }

    // Member by member, each confirmed by read-back. _shadowLive carries the values already written this pass, so a
    // later member's write never puts back an earlier member's old value.
    private void ApplyShadows(bool high)
    {
        if (_backend.ReadShadows() is not ShadowValues cur) return;
        _shadowLive = cur;
        Note("shadow resolution", _shadowRes.Step(cur.Resolution, high ? HighShadowResolution : null,
            v => WriteShadowMember(_shadowLive with { Resolution = v }, s => s.Resolution)));
        Note("shadow cascades", _cascades.Step(cur.Cascades, high ? HighShadowCascades : null,
            v => WriteShadowMember(_shadowLive with { Cascades = v }, s => s.Cascades)));
        Note("soft shadows", _soft.Step(cur.Soft, high ? true : null,
            v => WriteShadowMember(_shadowLive with { Soft = v }, s => s.Soft)));
    }

    private (bool Ok, T? After) WriteShadowMember<T>(ShadowValues next, Func<ShadowValues, T> member) where T : struct
    {
        var ok = _backend.WriteShadows(next);
        var after = _backend.ReadShadows();
        _shadowLive = after ?? (ok ? next : _shadowLive);
        return (ok, after is ShadowValues a ? member(a) : null);
    }

    private void Note(string lever, LeverOutcome outcome)
    {
        if (outcome == LeverOutcome.Failed && _warned.Add(lever))
            _warn($"Could not set {lever}: the game did not take the value (it is retried on the next scene change).");
    }

    private void Release(Token t)
    {
        if (_tokens.Remove(t)) Apply();
    }

    private void EndSuspension()
    {
        if (_captureSuspends == 0) return;
        _captureSuspends--;
        Apply();
    }

    private sealed class Token : IDisposable
    {
        private RenderQualityService? _svc;
        public Token(RenderQualityService svc, bool supersample, bool highShadows, object? owner)
        {
            _svc = svc;
            Supersample = supersample;
            HighShadows = highShadows;
            Owner = owner;
        }
        public bool Supersample { get; }
        public bool HighShadows { get; }
        public object? Owner { get; }
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }

    private sealed class Suspension : IDisposable
    {
        private RenderQualityService? _svc;
        public Suspension(RenderQualityService svc) => _svc = svc;
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.EndSuspension();
        }
    }
}
