using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Null-object <see cref="IPluginDownloads"/> — every request fails immediately with "not available" and
/// the data folder is empty. Only the shared aggregator holds it, as a fallback: every plugin gets its own real
/// download service from the per-plugin factory, and <c>PerPluginServices</c> falls back to this only when a
/// composition supplies no factory (tests).
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
