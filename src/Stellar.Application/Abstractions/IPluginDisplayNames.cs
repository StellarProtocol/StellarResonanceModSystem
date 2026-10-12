namespace Stellar.Application.Abstractions;

/// <summary>
/// Display-only plugin name for Settings surfaces. Ids, config keys, logs and sort keys stay on the internal
/// name; only the text a row DRAWS goes through this. Call it from render-driven row Funcs only — it may run
/// plugin code (a launcher entry's <c>TitleProvider</c>), which reads the active language.
/// </summary>
internal interface IPluginDisplayNames
{
    /// <summary>The plugin's localized display name, or <paramref name="fallback"/> (its internal name) when the
    /// plugin registered no launcher entry or the entry's title provider fails.</summary>
    string Resolve(string pluginId, string fallback);
}
