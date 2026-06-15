using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Mints one <see cref="PluginConfigService"/> per plugin GUID. The shared
/// <see cref="IConfigStore"/> handles the disk side; each created service
/// owns its own JSON tree keyed by the plugin's GUID, so section names
/// from different plugins cannot collide and SectionChanged events stay
/// scoped to the plugin that owns the file.
/// </summary>
internal sealed class PluginConfigFactory : IPluginConfigFactory
{
    private readonly IConfigStore _store;

    public PluginConfigFactory(IConfigStore store)
    {
        _store = store;
    }

    public IPluginConfig Create(string pluginGuid)
        => new PluginConfigService(_store, pluginGuid);
}
