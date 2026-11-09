using Stellar.Application.Services;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // Hidden developer mode (2.30.0): gates the Settings hub's Developer tab and the Shift+End perf-overlay hotkey.
    // Stored in stellar.framework.config.json → "developer": { "developerMode": bool }, default off.
    private DeveloperModePrefs? _developerMode;

    // Called from BuildNativeUiServices (Load), so the flag exists before the perf hotkey and the Settings panels wire.
    private void BuildDeveloperMode()
    {
        var config = _pluginConfigService!;
        var dev = _developerMode = new DeveloperModePrefs(config.GetSection(DeveloperModePrefs.SectionName));
        // A hand edit of the config file applies live (SectionChanged is raised on the game thread by the store's
        // drain); our own writes are SaveQuiet, and a same-value reload is a no-op anyway.
        config.SectionChanged += name => { if (name == DeveloperModePrefs.SectionName) dev.Reload(); };
        // The perf overlay is a developer tool: turning developer mode off closes it (the hub leaves the tab itself).
        dev.Changed += on => { if (!on && _perfOverlayControl?.IsShown == true) _perfOverlayControl.SetVisible(false); };
    }

    // Shift+End: a no-op unless developer mode is on (the action stays registered + rebindable in Settings → Hotkeys).
    private void TogglePerfOverlay()
    {
        if (_developerMode?.Enabled != true || _perfOverlayControl == null) return;
        _perfOverlayControl.SetVisible(!_perfOverlayControl.IsShown);
    }

    // The Developer tab's perf toggle. A show from hidden lands on top on its own (fresh activation) — no BringToFront.
    private bool PerfOverlayShown() => _perfOverlayControl?.IsShown == true;

    private void SetPerfOverlayShown(bool on) => _perfOverlayControl?.SetVisible(on);
}
