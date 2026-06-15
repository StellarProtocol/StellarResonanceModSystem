using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Theme;
using Stellar.Infrastructure.UI;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private EmbeddedAssetProvider? _assetProvider;
    private ThemeRenderer? _themeRenderer;
    // B-04: _namedTheme is constructed first in BuildThemeAndColorStack so the field-ownership
    // is explicit and local — no implicit cross-partial ordering dependency (B-04).
    // Logically a Phase 9a service; built here because ThemeRenderer takes it as a ctor dep.
    private NamedThemeService? _namedTheme;
    // Phase 9b.5 colour-registry stack — constructed in BuildThemeAndColorStack; the editor
    // (ThemesPanel, wired in Phase9/Wiring.Settings.cs) consumes _colorRegistry (as IThemeOverrides)
    // + _customThemes (ICustomThemeStore) + _namedTheme.
    private ColorRegistryService? _colorRegistry;
    private CustomThemeStore? _customThemes;

    // Theme renderer + Phase 9b.5 colour-registry stack. Built here so
    // ThemeRenderer can take INamedTheme + the resolver as ctor deps. Theme
    // assets (HudThemeAssets / WindowThemeAssets) bake themselves on demand the
    // first time a HUD or window is mounted — no OnGUI sink is needed. Framework
    // chrome slots are registered BEFORE the renderer so PresetColorsView resolves
    // them (an unregistered editable token would hit the magenta sentinel). One
    // ColorRegistryService serves as both IColorRegistry (plugin-facing) and
    // IColorResolver (read side).
    // B-04: NamedThemeService is constructed FIRST so all downstream ctors (ColorRegistryService,
    // ThemeRenderer) receive a fully-initialised instance.
    private void BuildThemeAndColorStack(BepInExPluginLog log)
    {
        _assetProvider = new EmbeddedAssetProvider();
        var themeSection     = _pluginConfigService!.GetSection("theme");
        var overridesSection = _pluginConfigService!.GetSection("themeOverrides");
        var customSection    = _pluginConfigService!.GetSection("customThemes");
        _namedTheme = new NamedThemeService(themeSection, log);
        var overrideStore = new ColorOverrideStore(overridesSection);
        _colorRegistry = new ColorRegistryService(_namedTheme, overrideStore);
        _customThemes  = new CustomThemeStore(customSection);
        FrameworkColorRegistration.RegisterAll(_colorRegistry);
        _themeRenderer = new ThemeRenderer(_assetProvider!, log, _namedTheme, _colorRegistry, _colorRegistry, _namedTheme);
    }
}
