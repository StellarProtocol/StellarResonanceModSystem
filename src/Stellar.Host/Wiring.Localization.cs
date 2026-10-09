using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
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
    /// aggregator + plugin host are built. The client language is UNKNOWN (served as English) until the game
    /// signals it is set — see <see cref="HookClientLanguageSetter"/> — then <c>follow</c> re-resolves live.
    /// </summary>
    private void BuildLocalization(BepInExPluginLog log)
    {
        var section = _pluginConfigService!.GetSection("localization");
        var engine = new LocalizationEngine(section, EnsureClientLanguage(log), log);
        foreach (var (code, json) in FrameworkCatalogs.Read())
            engine.RegisterCatalog(PluginGuid, code, json);
        _localizationEngine = engine;
        _frameworkLocalization = new PluginLocalization(engine, PluginGuid);

        if (_windowRenderer != null) engine.LanguageChanged += _windowRenderer.InvalidateTheme;
        if (_toastRenderer != null) engine.LanguageChanged += _toastRenderer.InvalidateTheme;
        // Theme-editor colour slot labels (Accent, Panel background, …) are plain strings baked by
        // FrameworkColorRegistration.RegisterAll at Load() time with an English fallback — ThemesPanel owns
        // relabeling them (lazily on its first poll, and again on every later switch) once it exists;
        // see ThemesPanel.RelabelFrameworkColors / ColorReg's doc.
    }

    /// <summary>The one client-language latch, shared by the localization engine (follow) and the game-data probe
    /// (design-label fallback). Created on first use by either wiring path.</summary>
    private ClientLanguageLatch EnsureClientLanguage(BepInExPluginLog log)
        => _clientLanguage ??= new ClientLanguageLatch(new PandaClientLanguage(_gameTypeRegistry!), log);

    /// <summary>
    /// Latch signals for the client language (2.21.0 fix — the game's index reads 0 = zh_Hans until its
    /// LocalizationMgr initialises, which is AFTER this wiring): a postfix on the game's own language setter
    /// (the static <c>CurrentLanguageTypeIndex</c> setter + <c>SetLanguage</c>) latches the value the game just
    /// set — and tracks a later in-game switch (the UI re-resolve is raised from the framework tick's drain, not inside
    /// the game's setter); then one opportunistic read in case the game set it before these
    /// hooks existed. The backstop is <c>Game.OnLogin</c> (character select — the language is certainly set by then).
    /// </summary>
    private void HookClientLanguageSetter(BepInExPluginLog log, HarmonyGameMethodHooker hooker, ReflectionGameTypeRegistry typeRegistry)
    {
        var latch = EnsureClientLanguage(log);
        var mgr = typeRegistry.FindType(PandaClientLanguage.LocalizationMgrTypeName);
        if (mgr != null)
        {
            hooker.PostfixStaticOverloads(mgr, "set_CurrentLanguageTypeIndex", (_, _) => latch.OnLanguageSet("game-setter"));
            hooker.PostfixAllOverloads(mgr, "SetLanguage", (_, _) => latch.OnLanguageSet("game-SetLanguage"));
        }
        else
        {
            log.Warning($"[Stellar][i18n] {PandaClientLanguage.LocalizationMgrTypeName} not found — follow resolves at character select only");
        }
        latch.TryLatchNonDefault("hook-install");
    }
}
