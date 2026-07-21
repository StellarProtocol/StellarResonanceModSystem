using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Mints one <see cref="IPluginDataStore"/> per plugin GUID (mirrors <see cref="IPluginConfigFactory"/>).</summary>
internal interface IPluginDataStoreFactory
{
    IPluginDataStore Create(string pluginGuid);
}
