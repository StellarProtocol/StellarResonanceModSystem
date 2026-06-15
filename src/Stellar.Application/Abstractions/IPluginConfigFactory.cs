using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Builds per-plugin <see cref="IPluginConfig"/> instances. PluginHost calls
/// this on each plugin load; the returned instance is unique to that plugin's
/// GUID and persists to a dedicated JSON file on disk
/// (<c>&lt;plugins-dir&gt;/&lt;pluginGuid&gt;.config.json</c>).
/// </summary>
internal interface IPluginConfigFactory
{
    IPluginConfig Create(string pluginGuid);
}
