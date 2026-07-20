using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Configuration;

/// <summary>Creates a <see cref="PluginDataStore"/> under <c>&lt;baseDir&gt;/&lt;guid&gt;.data/</c> per plugin.</summary>
internal sealed class PluginDataStoreFactory : IPluginDataStoreFactory
{
    private readonly string _baseDirPath;
    private readonly IPluginLog _log;

    public PluginDataStoreFactory(string baseDirPath, IPluginLog log)
    {
        _baseDirPath = baseDirPath;
        _log = log;
    }

    public IPluginDataStore Create(string pluginGuid) => new PluginDataStore(_baseDirPath, pluginGuid, _log);
}
