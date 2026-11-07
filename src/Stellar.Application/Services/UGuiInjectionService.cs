using System.Collections.Generic;
using Stellar.Abstractions.Domain;
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
    private readonly IClientState _clientState;
    private readonly List<Registration> _regs = new();
    private float _sinceTick;

    public UGuiInjectionService(IUGuiCanvasAdapter adapter, IClientState clientState)
    {
        _adapter = adapter;
        _clientState = clientState;
    }

    // Each anchor's host container only exists in certain client phases; probing it elsewhere is pure waste
    // (the service now ticks in EVERY phase from its un-gated call site, so without this the LoginSidebar
    // anchor would GameObject.Find in-world and the in-world anchors would probe at the title screen). Skipping
    // out-of-phase needs no manual teardown — the game destroys the injected element when its parent view is
    // torn down; we simply stop re-probing until the phase returns.
    private static bool IsAnchorRelevant(NativeUiAnchor anchor, GamePhase phase) => anchor switch
    {
        NativeUiAnchor.LoginSidebar => phase == GamePhase.TitleScreen,   // login view exists only at the title screen
        NativeUiAnchor.MainMenuRail => phase == GamePhase.World,         // ESC main-menu rail is in-world only
        NativeUiAnchor.HudTopRight  => phase == GamePhase.World,         // world HUD is in-world only
        _                           => true,                            // unknown anchors: probe every phase
    };

    public INativeUiElementHandle Register(NativeUiElementSpec spec) => RegisterCore(spec, branded: false);

    /// <summary>
    /// Framework-only (the composition root's "Stellar" button): registers <paramref name="spec"/> as BRANDED —
    /// accent-tinted icon + pulsing halo, and placed ahead of every plugin rail button. Plugins reach only the
    /// public <see cref="Register"/>, so their buttons are always plain.
    /// </summary>
    internal INativeUiElementHandle RegisterBranded(NativeUiElementSpec spec) => RegisterCore(spec, branded: true);

    private INativeUiElementHandle RegisterCore(NativeUiElementSpec spec, bool branded)
    {
        var reg = new Registration(spec, branded);
        _regs.Add(reg);
        return new Handle(this, reg);
    }

    // The rail slot for reg: how many live (non-removed) rail buttons sort ahead of it — branded first, then
    // registration order. Computed from _regs rather than the scene so a re-inject lands in the same place no
    // matter which button the game happened to rebuild first, and a removed button frees its slot (the
    // survivors close the gap on their next re-inject, i.e. the next menu open).
    private int RailSlotOf(Registration reg)
    {
        if (reg.Spec.Anchor != NativeUiAnchor.MainMenuRail || reg.Spec is not MenuButtonSpec) return 0;
        var slot = 0;
        var seenSelf = false;
        foreach (var other in _regs)
        {
            if (other == reg) { seenSelf = true; continue; }
            if (other.Removed || other.Spec.Anchor != NativeUiAnchor.MainMenuRail || other.Spec is not MenuButtonSpec) continue;
            // Branded beats plain whatever the order; otherwise the earlier registration is ahead.
            var ahead = other.Branded != reg.Branded ? other.Branded : !seenSelf;
            if (ahead) slot++;
        }
        return slot;
    }

    public void Tick(float deltaTime)
    {
        _sinceTick += deltaTime;
        if (_sinceTick < IntervalSeconds) return;
        _sinceTick = 0f;

        var phase = _clientState.Phase;
        foreach (var reg in _regs)
        {
            if (reg.Removed) continue;
            // Skip the resolve/inject entirely when the anchor's host can't exist in this phase — zero probing
            // cost out-of-phase (before any GameObject.Find / ResolveParent work).
            if (!IsAnchorRelevant(reg.Spec.Anchor, phase)) continue;
            if (!_adapter.IsAlive(reg.ElementRef))
            {
                reg.ElementRef = null;
                if (_adapter.IsAnchorAvailable(reg.Spec.Anchor))
                {
                    reg.ElementRef = _adapter.Inject(reg.Spec, new UGuiInjectContext(RailSlotOf(reg), reg.Branded));
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
        public Registration(NativeUiElementSpec spec, bool branded) { Spec = spec; Branded = branded; }
        public NativeUiElementSpec Spec { get; }
        public bool Branded { get; }
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
