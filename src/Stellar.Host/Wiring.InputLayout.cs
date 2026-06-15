using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.UI;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private UnityInputGateway? _inputGateway;
    private HotkeyService? _hotkeyService;
    private LayoutStorage? _layoutStorage;
    private LayoutEditorService? _layoutEditor;
    private LayoutEditorOverlay? _layoutOverlay;

    private void BuildInputAndLayoutServices(BepInExPluginLog log)
    {
        _inputGateway  = new UnityInputGateway();
        // _inputGateway.DiagnosticLog = log.Info;  // enable to log every captured keypress + modifier flags (off in production)

        // HotkeyService now persists user-bound keys via the framework's
        // "hotkeys" config section; missing keys fall back to SuggestedDefault.
        var hotkeySection = _pluginConfigService!.GetSection("hotkeys");
        _hotkeyService = new HotkeyService(_inputGateway, log, hotkeySection);
        _layoutStorage = new LayoutStorage(_pluginConfigService!, log);
        _layoutEditor  = new LayoutEditorService(_layoutStorage, log);

        _menuState = new Stellar.Infrastructure.Game.PandaMenuStateProbe();
        // Perf harness: route PerfProbe's periodic summary lines to the framework
        // log so the numbers are readable headlessly (scenario runs / log tail),
        // not only on the on-screen overlay. No-op unless STELLAR_PERFHUD=1.
        Stellar.Abstractions.Diagnostics.PerfProbe.LogSink = log.Info;
        _layoutOverlay = new LayoutEditorOverlay(_layoutEditor, _inputGateway, _layoutStorage, _themeRenderer!, log);

        // Framework-level edit-mode hotkey (Shift+` toggles layout edit mode).
        // Previously tried Shift+F12, Ctrl+F1, and Shift+F1 — all failed to fire
        // in-game. Switching to a non-F key (backtick) to rule out F-key-specific
        // input swallowing under Wine/IL2CPP. Diagnostic log in
        // UnityInputGateway.TickPoll will surface what the gateway actually sees.
        _hotkeyService.DeclareAction(
            new HotkeyAction(
                Id: "framework.layout-edit",
                Description: "Toggle layout edit mode",
                SuggestedDefault: new KeyBinding(StellarKeyCode.BackQuote, ModifierKeys.Shift)),
            callback: () => _layoutEditor.ToggleEditMode());
    }
}
