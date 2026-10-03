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
    /// thread too (never directly from a worker thread), at most once per tick and only when it changed. Progress is
    /// normally delivered before completion; a final 1.0 can arrive after a successful download while a photo capture
    /// is in progress (the final value — or nothing, on failure — is delivered exactly once). Never throws except on
    /// cancellation: a malformed request (null request, Url, Sha256 or path; a relative Url; a path outside the data
    /// folder) completes with an "invalid request" result.</summary>
    Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<double>? progress, CancellationToken ct);
    /// <summary>Absolute path of this plugin's data folder (where downloads land — the same folder
    /// <c>IPluginDataStore</c> uses). It may not exist until the first download.</summary>
    string DataFolder { get; }
}
