namespace Stellar.Application.Abstractions;

/// <summary>
/// Bundle of the per-plugin resource factories handed to <c>PluginHost</c>. Bundled into one
/// parameter so the host constructor stays within the STELLAR0004 six-dependency cap as the
/// localization host joins the existing config + data-store factories.
/// </summary>
internal readonly record struct PerPluginResourceFactories(
    IPluginConfigFactory Config,
    IPluginDataStoreFactory DataStore,
    ILocalizationHost Localization);
