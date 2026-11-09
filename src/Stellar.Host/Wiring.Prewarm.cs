using Stellar.Abstractions.Domain;

namespace Stellar.Host;

// One-shot PRE-WARM of the framework's two big lazily-built windows (WindowPrewarm; WindowBuilder-Patterns.md "PRE-WARM a
// lazily-built window"). Left lazy, each first open built its whole tree inside that frame, measured at 2560×1440:
//   • launcher (settings.hub)       [Hitch] frame=193ms (mount 149 ms, build:firstApply 149 ms)
//   • Settings hub (settings.ugui)  [Hitch] frame=316ms (mount 235 ms, build:firstApply 230 ms)
// We mount each once in the background instead, with ShouldRender forced false so the built root is never activated
// ON SCREEN: the renderer keeps it active 100000 units off-screen until its background work is done (time-sliced tab
// builds laid out + parked, bubbles settled), then it parks. Parked right after the mount, that work landed just after the
// hub's first open instead (79.6 / 89.5 / 64.4 ms frames, 11-09; WindowRenderer.BackgroundWarm.cs).
//
// ONE WINDOW AT A TIME: the launcher first (it is opened first — the hub is reached from it — and it is also usable at
// the title screen from the login-sidebar button), the hub only once the launcher's warm-up has settled. Each mount
// therefore lands on its own tick, never two ~150-230 ms builds in one frame.
//
// Z-order of visible windows is untouched: the warm-up never calls BringToFront. Its mount activates only the (hidden,
// off-screen) window itself — no pointer ever falls inside it, so FrontWindowBlocks and the raycaster never pick it, and
// the sticky cursor ignores it (BackgroundRoots) — and a parked root is inactive. The
// first real open still goes through ToggleLauncher / ShowSettingsHub: the un-park re-activates it (WindowStacking: on
// top, as if just opened) and BringToFront does the same for an open that lands mid-warm-up (already Visible, so no
// hidden→shown edge), exactly as on a cold mount.
//
// What gets built is what the window would open on: the launcher's Full vs compact body and its Favorites/All tab are
// Conditionals on the persisted LauncherRegistry mode/tab, the hub's start tab on its remembered tab — none depends on
// visibility, so the mount's first apply builds exactly those branches.
public sealed partial class BootstrapPlugin
{
    // Warming is AND-ed into each window's ShouldRender (stays deactivated) and into every "is it on screen" check
    // (ToggleLauncher; the hub's HubShown, which the help popover, colours child window and SettingsHub.Tick read).
    private readonly WindowPrewarm _launcherPrewarm = new("settings.hub");
    private readonly WindowPrewarm _hubPrewarm = new(Stellar.Infrastructure.UI.SettingsPanels.SettingsHubSpec.Id);

    // Pumped once per global tick from _themeEditorPoll, i.e. right AFTER WindowService.Tick (un-gated, every phase).
    private void PumpWindowPrewarms()
    {
        if (_windowService == null) return;
        // Launcher first; while it is pending or mounting, the hub waits (stagger). A launcher that never registered
        // (null control) doesn't hold the hub back.
        // The renderer keeps each one active off-screen until its background work is done (WindowPrewarm).
        if (_launcherControl != null && !_launcherPrewarm.Settled) { _launcherPrewarm.Tick(_launcherControl, CanStartPrewarm, _windowRenderer); return; }
        _hubPrewarm.Tick(_settingsHubControl, CanStartPrewarm, _windowRenderer);
    }

    // The environment gate shared by both warm-ups (each also skips itself when already shown / auto-opened).
    private bool CanStartPrewarm()
    {
        // Window canvas up with its CanvasScaler settled, so the mount succeeds and the anchored rect applies.
        if (!_windowService!.CanvasScaleReady) return false;
        // Not behind a loading screen (neither window renders there anyway; keep the build off a load frame), and
        // only once the login UI exists.
        if ((_clientState!.UiState & GameUIState.Loading) != 0 || _clientState.Phase == GamePhase.Startup) return false;
        // Build with the real client language, so the first paint bakes the right font/glyphs rather than English ones
        // swapped on the first open. Char select always latches it (OnLogin → OnLanguageSet).
        return _clientLanguage?.IsKnown != false || _clientState.Phase >= GamePhase.CharSelect;
    }
}
