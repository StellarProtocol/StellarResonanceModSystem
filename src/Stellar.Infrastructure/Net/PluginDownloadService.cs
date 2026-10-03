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
/// Progress is relayed the same way, through <see cref="MainThreadProgressQueue"/> (a sibling, independent
/// queue — see its own doc). Pure BCL IO + <see cref="HttpClient"/>; never throws except on a genuine
/// caller cancellation (an inactivity timeout maps to a normal <see cref="DownloadResult"/> instead).
/// </summary>
internal sealed class PluginDownloadService : IPluginDownloads
{
    private const int MaxInitialBufferBytes = 1024 * 1024; // 1 MiB — never balloon the upfront allocation
    private static readonly TimeSpan DefaultInactivityTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly IMainThreadResume _mainThreadResume;
    private readonly IPluginLog _log;
    private readonly MainThreadProgressQueue _progressQueue;
    private readonly TimeSpan _inactivityTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PluginDownloadService(string dataFolder, HttpClient http, IMainThreadResume mainThreadResume, IPluginLog log,
        MainThreadProgressQueue progressQueue, TimeSpan inactivityTimeout = default)
    {
        DataFolder = dataFolder;
        _http = http;
        _mainThreadResume = mainThreadResume;
        _log = log;
        _progressQueue = progressQueue;
        _inactivityTimeout = inactivityTimeout > TimeSpan.Zero ? inactivityTimeout : DefaultInactivityTimeout;
    }

    public string DataFolder { get; }

    public async Task<DownloadResult> DownloadAsync(DownloadRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        if (request.Url.Scheme != Uri.UriSchemeHttps) return new DownloadResult(false, null, "https only");
        var target = DownloadPlan.ResolveTarget(DataFolder, request.TargetFolder);
        if (target is null) return new DownloadResult(false, null, "folder not allowed");
        if (request.MaxBytes <= 0 || request.MaxBytes > Array.MaxLength) return new DownloadResult(false, null, "invalid size");
        if (!_gate.Wait(0)) return new DownloadResult(false, null, "busy");
        try
        {
            return await Task.Run(() => RunDownloadAsync(request, target, progress, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the caller's own token fired — propagate, per the interface contract
        }
        catch (OperationCanceledException)
        {
            // Not the caller's token: our own inactivity timeout fired instead of a real cancellation.
            return new DownloadResult(false, null, "timed out");
        }
        catch (Exception ex)
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

    // Fix round 2 (N2): the slot is FLUSHED exactly once on every exit path — 1.0 on success, dropped
    // (never delivered) on any failure — so no stale progress can arrive on a later tick after
    // DownloadAsync has already returned a result to the caller.
    private async Task<DownloadResult> RunDownloadAsync(DownloadRequest request, string target, IProgress<double>? progress, CancellationToken ct)
    {
        var slot = progress is null ? null : _progressQueue.CreateSlot(progress);
        try
        {
            var bytes = await DownloadBytesAsync(request.Url, request.MaxBytes, slot, ct).ConfigureAwait(false);
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            if (!string.Equals(actualHash, request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                _progressQueue.Finish(slot, null);
                return new DownloadResult(false, null, "checksum mismatch");
            }
            if (request.ExtractZip) ExtractZipAtomic(bytes, target, request.IncludePrefixes);
            else WriteFileAtomic(bytes, target);
            _progressQueue.Finish(slot, 1.0);
            return new DownloadResult(true, target, null);
        }
        catch
        {
            _progressQueue.Finish(slot, null);
            throw;
        }
    }

    // Linked to the caller's token so a genuine cancellation still propagates; CancelAfter is re-armed on
    // every read, so this is an INACTIVITY window (no bytes for _inactivityTimeout), not a total deadline.
    private async Task<byte[]> DownloadBytesAsync(Uri url, long maxBytes, MainThreadProgressQueue.Slot? slot, CancellationToken ct)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inactivity.CancelAfter(_inactivityTimeout);
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, inactivity.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"http {(int)response.StatusCode}");
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is { } declared && declared > maxBytes) throw new DownloadTooLargeException();
        var denom = Math.Max(contentLength ?? maxBytes, 1);
        await using var stream = await response.Content.ReadAsStreamAsync(inactivity.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream(capacity: (int)Math.Min(Math.Min(denom, maxBytes), MaxInitialBufferBytes));
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), inactivity.Token).ConfigureAwait(false)) > 0)
        {
            inactivity.CancelAfter(_inactivityTimeout); // bytes arrived — reset the inactivity window
            total += read;
            if (total > maxBytes) throw new DownloadTooLargeException();
            buffer.Write(chunk, 0, read);
            _progressQueue.Report(slot, Math.Min(0.99, total / (double)denom));
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

    private void WriteFileAtomic(byte[] bytes, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var tmp = target + ".download-tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tmp); // no-op once the move above has consumed it
        }
    }

    // Extracts into a temp sibling folder, then swaps it into place with a directory rename (SwapDirectory);
    // the temp folder is best-effort cleaned up on ANY failure (extraction or swap) so a failed download
    // never leaves a `.new-<guid>` sibling behind.
    private void ExtractZipAtomic(byte[] zipBytes, string target, IReadOnlyList<string>? includePrefixes)
    {
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, Path.GetFileName(target) + ".new-" + Guid.NewGuid().ToString("N"));
        try
        {
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
        finally
        {
            TryDeleteDirectory(temp); // no-op once the swap above has moved it into place
        }
    }

    /// <summary>
    /// Swaps <paramref name="temp"/> into <paramref name="target"/>'s place via two directory renames: move
    /// the old content (if any) aside, then move the new content in. If the second move fails, the old
    /// content is moved back before rethrowing — a failed download never leaves the plugin without its
    /// previous, working content. The old backup is deleted only after the swap succeeds, and that delete is
    /// best-effort (logged, never fails the download). Internal (not private) so a white-box test can drive
    /// the second-move-fails recovery path directly, without needing to fabricate a real filesystem lock.
    /// </summary>
    internal void SwapDirectory(string temp, string target)
    {
        string? oldBackup = null;
        if (Directory.Exists(target))
        {
            oldBackup = target + ".old-" + Guid.NewGuid().ToString("N");
            Directory.Move(target, oldBackup);
        }
        try
        {
            Directory.Move(temp, target);
        }
        catch
        {
            if (oldBackup is not null && Directory.Exists(oldBackup) && !Directory.Exists(target))
                Directory.Move(oldBackup, target);
            throw;
        }
        if (oldBackup is not null) TryDeleteDirectory(oldBackup);
    }

    private void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception ex) { _log.Warning($"[PluginDownloads] could not clean up {path}: {ex.GetType().Name}: {ex.Message}"); }
    }

    private void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { _log.Warning($"[PluginDownloads] could not clean up {path}: {ex.GetType().Name}: {ex.Message}"); }
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
