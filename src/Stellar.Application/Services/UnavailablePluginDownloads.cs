using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Null-object <see cref="IPluginDownloads"/> — every request fails immediately with "not available" and
/// the data folder is empty. Host wires this in (shared aggregator) and <c>PerPluginServices</c> forwards
/// it per-plugin until a later task replaces both with the real per-plugin download service.
/// </summary>
internal sealed class UnavailablePluginDownloads : IPluginDownloads
{
    /// <summary>Shared instance — this service holds no per-call state.</summary>
    public static readonly UnavailablePluginDownloads Instance = new();

    /// <inheritdoc/>
    public Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<double>? progress, CancellationToken ct) =>
        Task.FromResult(new DownloadResult(false, null, "not available"));

    /// <inheritdoc/>
    public string DataFolder => "";
}
