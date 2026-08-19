using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Localization;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // The single localization engine (also the ILocalizationControl the Settings dropdown drives) and the
    // framework's own scoped façade (namespace "stellar.framework"), consumed by the framework's UI.
    private LocalizationEngine? _localizationEngine;
    private ILocalization? _frameworkLocalization;

    /// <summary>
    /// Builds the localization engine: the persisted <c>localization.language</c> setting, the
    /// client-language probe (for the <c>follow</c> default), and the framework's own embedded catalog
    /// under <see cref="PluginGuid"/>. Subscribes the baked window/toast renderers to a live language
    /// change so their bitmap-baked text re-flushes (<c>Func&lt;string&gt;</c> labels re-poll on their own).
    /// Called at the top of <see cref="ConstructPluginServices"/> so the engine is ready before the
    /// aggregator + plugin host are built. The client-language read resolves lazily — before HybridCLR
    /// loads it returns English, then latches the real client language and <c>follow</c> tracks it live.
    /// </summary>
    private void BuildLocalization(BepInExPluginLog log)
    {
        var section = _pluginConfigService!.GetSection("localization");
        _clientLanguage ??= new PandaClientLanguage(log, _gameTypeRegistry!);
        var probe = new ClientLanguageProbe(_clientLanguage);
        var engine = new LocalizationEngine(section, probe, log);
        foreach (var (code, json) in FrameworkCatalogs.Read())
            engine.RegisterCatalog(PluginGuid, code, json);
        _localizationEngine = engine;
        _frameworkLocalization = new PluginLocalization(engine, PluginGuid);

        if (_windowRenderer != null) engine.LanguageChanged += _windowRenderer.InvalidateTheme;
        if (_toastRenderer != null) engine.LanguageChanged += _toastRenderer.InvalidateTheme;
    }
}
