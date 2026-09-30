using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

internal sealed class RenderLookService : IRenderLook
{
    private const float FocusEpsilon = 0.05f;
    private readonly ILookBackend _backend;
    private readonly Action<string> _warn;
    private Handle? _active;
    private LookSettings? _current;
    private float? _trackedFocus;   // last measured camera→player distance while a look tracks the player

    public RenderLookService(ILookBackend backend, Action<string> warn)
    {
        _backend = backend;
        _warn = warn;
    }

    public LookCapabilities Capabilities => _backend.Capabilities;

    public ILookHandle Apply(LookSettings settings)
    {
        if (_active is not null)
        {
            _warn("A second look was applied; the previous look was replaced.");
            _active.Deactivate();
        }
        _active = new Handle(this);
        Push(settings);
        return _active;
    }

    public void Tick()
    {
        if (_current?.Dof is not { FocusOnLocalPlayer: true } dof) return;
        if (_backend.MeasureFocusDistance() is not float d) return;
        if (Math.Abs(d - dof.FocusDistance) < FocusEpsilon) return;
        _trackedFocus = d;
        _current = _current with { Dof = dof with { FocusDistance = d } };
        _backend.UpdateFocus(d);
    }

    internal static LookSettings Effective(LookSettings s, LookGroups supported)
    {
        var e = s.PlayMode ? s with { Dof = null, FilmGrain = null } : s;
        bool Has(LookGroups g) => (supported & g) != 0;
        return e with
        {
            Dof = Has(LookGroups.Dof) ? e.Dof : null,
            Color = Has(LookGroups.Color) ? e.Color : null,
            WhiteBalance = Has(LookGroups.WhiteBalance) ? e.WhiteBalance : null,
            Lut = Has(LookGroups.Lut) ? e.Lut : null,
            Bloom = Has(LookGroups.Bloom) ? e.Bloom : null,
            Vignette = Has(LookGroups.Vignette) ? e.Vignette : null,
            FilmGrain = Has(LookGroups.FilmGrain) ? e.FilmGrain : null,
        };
    }

    private void Push(LookSettings s)
    {
        // An update while tracking keeps the tracked distance (no one-frame jump to the settings' default).
        if (s.Dof is { FocusOnLocalPlayer: true } d && _trackedFocus is float f) s = s with { Dof = d with { FocusDistance = f } };
        _current = Effective(s, _backend.Capabilities.Supported);
        _backend.Apply(_current);
    }

    private void Release(Handle h)
    {
        if (!ReferenceEquals(h, _active)) return;
        _active = null;
        _current = null;
        _trackedFocus = null;
        _backend.Apply(null);
    }

    private sealed class Handle : ILookHandle
    {
        private RenderLookService? _svc;
        public Handle(RenderLookService svc) => _svc = svc;
        public bool IsActive => _svc is not null;
        public void Update(LookSettings settings) => _svc?.Push(settings);
        public void Deactivate() => _svc = null;
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
