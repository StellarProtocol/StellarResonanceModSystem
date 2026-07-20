using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Configuration;

/// <summary>Creates a <see cref="PluginDataStore"/> under <c>&lt;pluginsDir&gt;/&lt;guid&gt;.data/</c> per plugin.</summary>
internal sealed class PluginDataStoreFactory : IPluginDataStoreFactory
{
    private readonly string _pluginsDirPath;
    private readonly IPluginLog _log;

    public PluginDataStoreFactory(string pluginsDirPath, IPluginLog log)
    {
        _pluginsDirPath = pluginsDirPath;
        _log = log;
    }

    public IPluginDataStore Create(string pluginGuid) => new PluginDataStore(_pluginsDirPath, pluginGuid, _log);
}
