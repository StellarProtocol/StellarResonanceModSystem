using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Downloads and verifies one plugin-requested file or zip into the plugin's own data folder, one at a time.</summary>
public interface IPluginDownloads
{
    /// <summary>Downloads, verifies (sha256 before anything is written) and places the request under this plugin's data
    /// folder. One download per plugin at a time. Completes on the main thread; progress is reported on the main
    /// thread too (never directly from a worker thread), at most once per tick and only when it changed; never
    /// throws except on cancellation.</summary>
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Absolute path of this plugin's data folder (where downloads land).</summary>
    string DataFolder { get; }
}
