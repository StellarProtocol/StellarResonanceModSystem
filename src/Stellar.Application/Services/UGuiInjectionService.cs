using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Owns mod-uGUI registrations and drives (re)injection through
/// <see cref="IUGuiCanvasAdapter"/>. Injects when an element's anchor becomes
/// available, re-injects after the game destroys it (menu close), refreshes
/// dynamic content, and destroys everything on framework dispose.
/// </summary>
/// <remarks>
/// <see cref="Tick"/> is wired to the per-frame Framework.Update but gates its
/// work to <see cref="IntervalSeconds"/> (~5 Hz): anchor (re)probes via
/// <c>GameObject.Find</c> and dynamic content re-pulls are lifecycle/refresh
/// events, not per-frame ones, so running them every frame is pure waste.
/// <c>handle.Update()</c> still forces an immediate content re-pull between ticks.
/// </remarks>
internal sealed class UGuiInjectionService : INativeUiHost
{
    private const float IntervalSeconds = 0.2f; // ~5 Hz probe + refresh cadence

    private readonly IUGuiCanvasAdapter _adapter;
    private readonly List<Registration> _regs = new();
    private float _sinceTick;

    public UGuiInjectionService(IUGuiCanvasAdapter adapter)
    {
        _adapter = adapter;
    }

    public INativeUiElementHandle Register(NativeUiElementSpec spec)
    {
        var reg = new Registration(spec);
        _regs.Add(reg);
        return new Handle(this, reg);
    }

    public void Tick(float deltaTime)
    {
        _sinceTick += deltaTime;
        if (_sinceTick < IntervalSeconds) return;
        _sinceTick = 0f;

        foreach (var reg in _regs)
        {
            if (reg.Removed) continue;
            if (!_adapter.IsAlive(reg.ElementRef))
            {
                reg.ElementRef = null;
                if (_adapter.IsAnchorAvailable(reg.Spec.Anchor))
                {
                    reg.ElementRef = _adapter.Inject(reg.Spec);
                    if (reg.ElementRef != null) _adapter.ApplyContent(reg.ElementRef, reg.Spec);
                }
            }
            else
            {
                _adapter.ApplyContent(reg.ElementRef, reg.Spec);
            }
        }
    }

    public void OnFrameworkDispose()
    {
        foreach (var reg in _regs)
        {
            if (reg.ElementRef != null) _adapter.Destroy(reg.ElementRef);
            reg.ElementRef = null;
            reg.Removed = true;
        }
        // Release any adapter-owned resources (e.g. the rail button's icon textures).
        (_adapter as System.IDisposable)?.Dispose();
    }

    private void Remove(Registration reg)
    {
        if (reg.ElementRef != null) _adapter.Destroy(reg.ElementRef);
        reg.ElementRef = null;
        reg.Removed = true;
    }

    private void ForceUpdate(Registration reg)
    {
        if (!reg.Removed && reg.ElementRef != null) _adapter.ApplyContent(reg.ElementRef, reg.Spec);
    }

    private sealed class Registration
    {
        public Registration(NativeUiElementSpec spec) => Spec = spec;
        public NativeUiElementSpec Spec { get; }
        public object? ElementRef { get; set; }
        public bool Removed { get; set; }
    }

    private sealed class Handle : INativeUiElementHandle
    {
        private readonly UGuiInjectionService _svc;
        private readonly Registration _reg;
        public Handle(UGuiInjectionService svc, Registration reg) { _svc = svc; _reg = reg; }
        public bool IsInjected => !_reg.Removed && _reg.ElementRef != null;
        public void Update() => _svc.ForceUpdate(_reg);
        public void Remove() => _svc.Remove(_reg);
    }
}
