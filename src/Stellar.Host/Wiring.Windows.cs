using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private WindowService? _windowService;
    private WindowRenderer? _windowRenderer;

    private void BuildWindowServices(BepInExPluginLog log)
    {
        // uGUI interactive window toolkit (SP1 window shell). Mirrors the HUD wiring: the
        // renderer bakes the frosted GlassMenu chrome from the active menu palette + owns the
        // window canvas (+ GraphicRaycaster, riding the game EventSystem). Injected into the
        // PluginServices aggregator below; the theme-switch hook rebakes + re-mounts next tick.
        _windowRenderer = new WindowRenderer(log, _themeRenderer!.Colors, _themeRenderer!.Colors, _namedTheme!);
        _windowService = new WindowService(_windowRenderer, log, _layoutEditor);
        _namedTheme!.ActiveChanged += _windowRenderer.InvalidateTheme;
        WireStickyCursor(log);
    }

    // Always-on: a press on a Stellar window must not count as a press INSIDE the game UI under it. The game's
    // ZUIPointPressCheck (chat input, tips, outside-press popups) tests containment over every raycast hit, not just the
    // top one, so our canvas alone didn't stop it — clicking a window over the chat area activated the hidden chat input.
    // Installed from ApplyLifecyclePatches (OnHotUpdateReady), when the game's UI types are loaded.
    private void InstallGamePressCheckShield(BepInExPluginLog log)
    {
        _frameworkHarmonyHost ??= _harmonyHostFactory!.Create("stellar.framework");
        GamePressCheckShield.Install(_frameworkHarmonyHost, log.Info);
    }

    // Always-on: the game forces IME Off every frame from DefaultMousePointer.HideMousePoint, so the Korean 한/영 key is
    // dead in a Stellar field. The prefix skips that write only while one of OUR fields is focused, the same signal that
    // drives KeyboardInputGate. Installed from ApplyLifecyclePatches (OnHotUpdateReady), when Panda.Script types are loaded.
    private void InstallImeKeepOnPatch(BepInExPluginLog log)
    {
        _frameworkHarmonyHost ??= _harmonyHostFactory!.Create("stellar.framework");
        ImeKeepOnPatch.Install(_frameworkHarmonyHost, () => _windowService?.AnyFieldFocused ?? false, log.Info);
    }

    // Opt-in sticky cursor (Settings → Layout). Ticked per rendered frame by the window ticker; the lambdas defer, so the
    // input shield (WireFreeCamera, built later) and the post-hot-update game types are read only when first needed.
    private void WireStickyCursor(BepInExPluginLog log)
    {
        var mouseGate = new RewiredMouseGate(_gameTypeRegistry!, log);
        // World model-picking runs through the game's touch layer, not Rewired: patched lazily on first engage, through the
        // framework's own Harmony host (auto-unpatched on framework teardown).
        _frameworkHarmonyHost ??= _harmonyHostFactory!.Create("stellar.framework");
        var pointerGate = new WorldPointerGate(_frameworkHarmonyHost, log);
        var backend = new StickyCursorBackend(_luaService!, _keyBlockPatch!, mouseGate, pointerGate);
        _windowRenderer!.StickyCursorDispose = () => { mouseGate.Dispose(); pointerGate.Dispose(); };
        _windowRenderer.StickyCursor = new Stellar.Infrastructure.Unity.WindowInteractionTicker.StickyHooks
        {
            Service = new StickyCursorService(backend, log.Info),
            Enabled = () => _layoutStorage?.StickyCursorEnabled ?? false,
            WorldActive = () => _clientState?.IsWorldActive ?? false,
            PointerOverGameUi = () => _inputShield?.IsPointerOverGameUi ?? false,
            Log = log.Info,
        };
    }
}
