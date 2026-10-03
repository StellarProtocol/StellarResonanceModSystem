using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Net;

/// <summary>
/// Real <see cref="IPluginDownloads"/>: downloads, verifies (sha256 before anything is written) and
/// places one https file or zip under this plugin's own data folder, one at a time. The whole
/// download/verify/place pipeline runs on a pool thread (<see cref="Task.Run(Func{Task})"/>) and resumes
/// on the main thread via <see cref="IMainThreadResume"/> before completing — the same mechanism
/// <c>ScreenCaptureService</c> uses, reused rather than re-invented (docs/il2cpp-probing-safety.md).
/// Pure BCL IO + <see cref="HttpClient"/>; never throws except on cancellation.
/// </summary>
internal sealed class PluginDownloadService : IPluginDownloads
{
    private readonly HttpClient _http;
    private readonly IMainThreadResume _mainThreadResume;
    private readonly IPluginLog _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginDownloadService(string dataFolder, HttpClient http, IMainThreadResume mainThreadResume, IPluginLog log)
    {
        DataFolder = dataFolder;
        _http = http;
        _mainThreadResume = mainThreadResume;
        _log = log;
    }

    public string DataFolder { get; }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        if (request.Url.Scheme != Uri.UriSchemeHttps) return new DownloadResult(false, null, "https only");
        var target = DownloadPlan.ResolveTarget(DataFolder, request.TargetFolder);
        if (target is null) return new DownloadResult(false, null, "folder not allowed");
        if (!_gate.Wait(0)) return new DownloadResult(false, null, "busy");
        try
        {
            return await Task.Run(() => RunDownloadAsync(request, target, progress, ct), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning($"[PluginDownloads] {request.Url}: {ex.GetType().Name}: {ex.Message}");
            return new DownloadResult(false, null, MapError(ex));
        }
        finally
        {
            _gate.Release();
            await ResumeQuietly().ConfigureAwait(false);
        }
    }

    private async Task<DownloadResult> RunDownloadAsync(DownloadRequest request, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var bytes = await DownloadBytesAsync(request.Url, request.MaxBytes, progress, ct).ConfigureAwait(false);
        var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actualHash, request.Sha256, StringComparison.OrdinalIgnoreCase))
            return new DownloadResult(false, null, "checksum mismatch");
        if (request.ExtractZip) ExtractZipAtomic(bytes, target, request.IncludePrefixes);
        else WriteFileAtomic(bytes, target);
        progress?.Report(1.0);
        return new DownloadResult(true, target, null);
    }

    private async Task<byte[]> DownloadBytesAsync(Uri url, long maxBytes, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"http {(int)response.StatusCode}");
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is { } declared && declared > maxBytes) throw new DownloadTooLargeException();
        var denom = Math.Max(contentLength ?? maxBytes, 1);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(denom, maxBytes));
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > maxBytes) throw new DownloadTooLargeException();
            buffer.Write(chunk, 0, read);
            progress?.Report(Math.Min(0.99, total / (double)denom));
        }
        return buffer.ToArray();
    }

    // A faulting/throwing resume must not escape DownloadAsync — the result (if any) is already decided
    // and the caller is owed it. Mirrors ScreenCaptureService's ResumeQuietly backstop.
    private async Task ResumeQuietly()
    {
        try { await _mainThreadResume.ResumeOnMainThreadAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Warning("[PluginDownloads] could not resume on the main thread: " + ex); }
    }

    private static void WriteFileAtomic(byte[] bytes, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".download-tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, target, overwrite: true);
    }

    // Extracts into a temp sibling folder, then swaps it into place with a directory rename; the old
    // folder (if any) is deleted only after the swap succeeds — a mid-extraction failure never touches
    // whatever was already there.
    private static void ExtractZipAtomic(byte[] zipBytes, string target, IReadOnlyList<string>? includePrefixes)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, Path.GetFileName(target) + ".new-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        using (var archive = new ZipArchive(new MemoryStream(zipBytes, writable: false), ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                var mapped = DownloadPlan.MapZipEntry(entry.FullName, includePrefixes);
                if (mapped is null) continue;
                var destPath = Path.Combine(temp, mapped.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }
        SwapDirectory(temp, target);
    }

    private static void SwapDirectory(string temp, string target)
    {
        string? oldBackup = null;
        if (Directory.Exists(target))
        {
            oldBackup = target + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(target, oldBackup);
        }
        Directory.Move(temp, target);
        if (oldBackup is not null) Directory.Delete(oldBackup, recursive: true);
    }

    private static string MapError(Exception ex) => ex switch
    {
        DownloadTooLargeException => "too large",
        InvalidDataException => "bad zip",
        HttpRequestException => "network error",
        IOException or UnauthorizedAccessException => "could not write to the data folder",
        _ => "download failed unexpectedly",
    };

    private sealed class DownloadTooLargeException : Exception
    {
        public DownloadTooLargeException() : base("too large") { }
    }
}
