using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.UI.SettingsPanels;

namespace Stellar.Host;

// One-shot Settings hub PRE-WARM (WindowBuilder-Patterns.md "PRE-WARM a lazily-built window", same recipe as the layout
// toolbar's PrewarmToolbar). Left lazy, the hub's first open built its whole tree inside that frame — measured
// [Hitch] frame=316ms (mount 235 ms, of which build:firstApply 230 ms) at 2560×1440; later opens only un-park it.
// We mount it once in the background instead, with ShouldRender forced false so the built root is never activated.
//
// Z-order is untouched: the warm-up never calls BringToFront (ZFront stays 0, as for a never-opened hub). The mount
// only hands the hub a ZSeq, and a parked root is inactive, so FrontWindowBlocks and the raycaster skip it. The first
// real open still goes through ShowSettingsHub: the un-park takes a fresh ZSeq and BringToFront takes the newest ZFront,
// exactly as it would on a cold mount (where BringToFrontPending does the same at mount). So "launcher then Settings"
// or "Settings then launcher" still leaves the most recently opened one on top.
public sealed partial class BootstrapPlugin
{
    //   _hubPrewarming — transient; AND-ed into the hub's ShouldRender (stays deactivated) and HubShown (the help popover,
    //                    the colours child window and SettingsHub.Tick's close handling all see "not shown").
    //   _hubPrewarmPhase — 0 = pending, 1 = mounted and waiting a few ticks, 2 = done (never runs again).
    private bool _hubPrewarming;
    private int _hubPrewarmPhase;
    private int _hubPrewarmTicks;

    // Pumped once per global tick from _themeEditorPoll, i.e. right AFTER WindowService.Tick (un-gated, every phase).
    private void PrewarmSettingsHub()
    {
        if (_hubPrewarmPhase == 2 || _settingsHubControl == null || _windowService == null) return;
        switch (_hubPrewarmPhase)
        {
            case 0:
                if (!CanStartHubPrewarm()) return;   // retry next tick
                _hubPrewarming = true;                 // ShouldRender false → mounts (builds the tree) but never activates
                _settingsHubControl.SetVisible(true);  // Visible=true → the next WindowService.Tick mounts it
                _hubPrewarmTicks = 0;
                _hubPrewarmPhase = 1;
                break;
            case 1:
                // A few ticks so the mount (+ its deferred layout apply) lands whatever the tick ordering. The mount's
                // first Apply builds the ACTIVE tab's Conditional (When = _tab == idx is visibility-independent), so the
                // tab the hub opens on (the remembered tab, or STELLAR_SETTINGS_TAB) is pre-built too.
                if (++_hubPrewarmTicks < 3) return;
                _settingsHubControl.SetVisible(false);   // park: tree kept, deactivated, reused by the real open
                _hubPrewarming = false;
                _hubPrewarmPhase = 2;
                break;
        }
    }

    private bool CanStartHubPrewarm()
    {
        // STELLAR_AUTO_OPEN may already have shown the hub by plain SetVisible(true) (not via ShowSettingsHub); a
        // warm-up would then hide it. Nothing to warm in that case.
        var autoOpen = System.Environment.GetEnvironmentVariable("STELLAR_AUTO_OPEN");
        if (autoOpen != null && autoOpen.Contains(SettingsHubSpec.Id)) { _hubPrewarmPhase = 2; return false; }
        if (_settingsHubControl!.IsShown) { _hubPrewarmPhase = 2; return false; }   // already open → already built
        // Window canvas up with its CanvasScaler settled, so the mount succeeds and the anchored rect applies.
        if (!_windowService!.CanvasScaleReady) return false;
        // Not behind a loading screen (the hub would not render there anyway; keep the 230 ms off a load frame), and
        // only once the login UI exists.
        if ((_clientState!.UiState & GameUIState.Loading) != 0 || _clientState.Phase == GamePhase.Startup) return false;
        // Build with the real client language, so the first paint bakes the right font/glyphs rather than English ones
        // swapped on the first open. Char select always latches it (OnLogin → OnLanguageSet).
        return _clientLanguage?.IsKnown != false || _clientState.Phase >= GamePhase.CharSelect;
    }

    // A real open during the warm-up takes over: drop the suppression and finish the one-shot BEFORE SetVisible(true),
    // and repaint now (the window may already be mounted and suppressed — without MarkDirty it would wait ≤100 ms for
    // the next capped apply).
    private void EndHubPrewarmForRealOpen()
    {
        if (_hubPrewarmPhase == 2) return;
        var wasMounting = _hubPrewarmPhase == 1;
        _hubPrewarming = false;
        _hubPrewarmPhase = 2;
        if (wasMounting) _settingsHubControl?.MarkDirty();
    }
}
