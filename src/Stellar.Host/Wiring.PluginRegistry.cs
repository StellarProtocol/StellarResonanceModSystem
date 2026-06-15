using Stellar.Abstractions.Services;
using Stellar.Application.Hosting;
using Stellar.Application.Services;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private PluginRegistry? _pluginRegistry;
    private Stellar.Abstractions.Services.IPluginServices? _capturedServices;

    private void DisposePhase9()
    {
        // Mod-isolation: restore every modified game HUD element to its
        // OriginalRect on host shutdown.
        _nativeUi?.OnFrameworkDispose();
        // Remove the Stellar rail button explicitly, then the bulk uGUI teardown.
        _railButtonHandle?.Remove();
        _uguiInjection?.OnFrameworkDispose();
        // The uGUI launcher tears down with the window canvas (WindowService.DisposeAll below); its icon
        // textures are reclaimed by WindowToken.DisposeNativeTextures on destroy.
        // uGUI HUD toolkit (Task 6): destroy any mounted HUDs on shutdown, unhook the
        // theme-switch rebake, then destroy the canvas + baked sprite assets.
        _hudService?.DisposeAll();
        if (_hudRenderer != null && _namedTheme != null) _namedTheme.ActiveChanged -= _hudRenderer.InvalidateTheme;
        _hudRenderer?.Shutdown();
        // uGUI interactive window toolkit (SP1): destroy mounted windows, unhook theme rebake, drop canvas.
        _windowService?.DisposeAll();
        if (_windowRenderer != null && _namedTheme != null) _namedTheme.ActiveChanged -= _windowRenderer.InvalidateTheme;
        _windowRenderer?.Shutdown();
        if (_keyboardGate != null) { _keyboardGate.Dispose(); _keyboardGate = null; }
    }
}
