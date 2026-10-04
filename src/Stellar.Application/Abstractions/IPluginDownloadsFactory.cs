using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Mints one <see cref="IPluginDownloads"/> per plugin GUID (mirrors <see cref="IPluginDataStoreFactory"/>).</summary>
internal interface IPluginDownloadsFactory
{
    IPluginDownloads Create(string pluginGuid);
}
