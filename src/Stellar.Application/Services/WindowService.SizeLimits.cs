using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

// Runtime resize limits (IWindowSizeLimits). The live limits live on the Entry, not the token, because a token dies on
// every remount (scene change, ShouldRender) and its chrome re-registers the grip from the immutable WindowSpec: TickEntry
// re-applies them right after the mount and BEFORE ApplySavedRect, so the saved-layout restore already clamps against
// them (a saved height that predates a locked axis is fixed on load, never shown for a frame).
internal sealed partial class WindowService
{
    // Fresh mount (TickEntry): the new token starts from the spec, so re-apply the runtime overrides. Split out of
    // TickEntry (STELLAR0002 50-LoC method cap).
    private void ReapplyRuntimeWindowState(Entry e)
    {
        if (e.Topmost != e.Reg.Spec.Topmost) _order?.SetTopmost(e.Token, e.Topmost);
        ApplySizeLimits(e);
    }

    // Optional renderer port (a test fake lacks it). Cast per call: this runs only on a limit change or a mount.
    private void ApplySizeLimits(Entry e)
    {
        if (e.Token == null || e.SizeLimits is not { } l || _renderer is not IWindowResizeLimits port) return;
        port.SetResizeLimits(e.Token, l.MinW, l.MinH, l.MaxW, l.MaxH);
    }

    private sealed partial class Entry : IWindowSizeLimits
    {
        // Null = the spec's limits (the common case; nothing re-applied at mount).
        public (float MinW, float MinH, float MaxW, float MaxH)? SizeLimits;

        public void SetSizeLimits(float minWidth, float minHeight, float maxWidth, float maxHeight)
        {
            if (!Reg.Spec.Resizable) return;   // only resizable windows have a grip / size clamp to change
            var minW = Math.Max(0f, minWidth); var minH = Math.Max(0f, minHeight);
            var next = (minW, minH, Math.Max(minW, maxWidth), Math.Max(minH, maxHeight));
            if (SizeLimits == next) return;   // idempotent: a plugin re-asserting its limits costs nothing
            SizeLimits = next;
            Owner.ApplySizeLimits(this);      // unmounted: TickEntry applies it at mount
        }
    }
}
