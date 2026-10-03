using System.IO;
using System.Net.Http;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Net;

/// <summary>
/// Creates one <see cref="PluginDownloadService"/> per plugin GUID, rooted at
/// <c>&lt;baseDir&gt;/&lt;pluginGuid&gt;.data/</c> — the same base dir
/// <see cref="Configuration.PluginDataStoreFactory"/> uses (mirrors it). Every minted service shares one
/// <see cref="HttpClient"/> and the framework's one main-thread resume mechanism.
/// </summary>
internal sealed class PluginDownloadsFactory : IPluginDownloadsFactory
{
    private readonly string _baseDirPath;
    private readonly HttpClient _http;
    private readonly IMainThreadResume _mainThreadResume;
    private readonly IPluginLog _log;
    private readonly MainThreadProgressQueue _progressQueue;

    public PluginDownloadsFactory(string baseDirPath, HttpClient http, IMainThreadResume mainThreadResume, IPluginLog log,
        MainThreadProgressQueue progressQueue)
    {
        _baseDirPath = baseDirPath;
        _http = http;
        _mainThreadResume = mainThreadResume;
        _log = log;
        _progressQueue = progressQueue;
    }

    public IPluginDownloads Create(string pluginGuid) =>
        new PluginDownloadService(Path.GetFullPath(Path.Combine(_baseDirPath, pluginGuid + ".data")), _http, _mainThreadResume, _log, _progressQueue);
}
