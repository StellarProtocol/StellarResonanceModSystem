using System.Collections.Generic;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

// ConfirmHostElement's two ticker-side cancels (the scrim + card are a LayerStack, WindowBuilder.ConfirmHost.cs):
//  • Esc cancels every OPEN confirm whose host is on screen. Polled per render frame — the window's 10 Hz apply would
//    miss a one-frame key edge (same reason as TickDismissables). Enter is deliberately NOT a confirm key.
//  • A host that LEAVES the screen while its confirm is open (the title-bar ✕ hid the window, the window was parked, or
//    a Conditional/tab hid the host) cancels it — "✕ = Cancel", no request left dangling behind a hidden window. Only a
//    shown→hidden edge for the SAME request counts: a confirm opened while the window is still hidden (plugin calls
//    Show, then SetVisible — a first mount takes a few frames) must survive until it has been on screen.
//    A parked window runs no apply, so this cannot live in a binding.
// Cost per frame: one Count check + one managed bool per registered host (IsOpen); Unity is touched only for an open one.
public sealed partial class WindowInteractionTicker
{
    internal sealed class ConfirmHostReg
    {
        public GameObject Root = null!;
        public ConfirmController Ctl = null!;
        public ConfirmRequest? Seen;   // the open request this host has been on screen with
    }

    internal readonly List<ConfirmHostReg> ConfirmHosts = new();

    private void TickConfirmHosts()
    {
        if (ConfirmHosts.Count == 0) return;
        var esc = false; var escRead = false;
        for (var i = 0; i < ConfirmHosts.Count; i++)
        {
            var reg = ConfirmHosts[i];
            var cur = reg.Ctl?.Current;
            if (cur == null || reg.Root == null) continue;
            if (reg.Root.activeInHierarchy)
            {
                reg.Seen = cur;
                if (!escRead) { esc = Input.GetKeyDown(KeyCode.Escape); escRead = true; }
                if (!esc) continue;
            }
            else if (!ReferenceEquals(reg.Seen, cur)) continue;   // never shown with this request yet: keep it
            try { reg.Ctl!.Cancel(); }
            catch { if (_throwLogged++ == 0) Debug.LogWarning("[Window] confirm OnCancel threw (rate-limited)"); }
        }
    }

    private void PruneConfirmHosts()
    {
        for (var i = ConfirmHosts.Count - 1; i >= 0; i--) if (ConfirmHosts[i].Root == null) ConfirmHosts.RemoveAt(i);
    }
}
