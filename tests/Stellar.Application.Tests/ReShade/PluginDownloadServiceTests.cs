using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Net;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

public sealed class PluginDownloadServiceTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { /* best-effort cleanup; never fail a test on teardown */ }
        }
    }

    private PluginDownloadService NewService(HttpMessageHandler handler, out string dataDir) =>
        NewService(handler, out dataDir, out _);

    private PluginDownloadService NewService(HttpMessageHandler handler, out string dataDir, out MainThreadProgressQueue progressQueue,
        TimeSpan inactivityTimeout = default)
    {
        var root = Path.Combine(Path.GetTempPath(), "stellar-dl-" + Path.GetRandomFileName());
        _tempRoots.Add(root);
        dataDir = Path.Combine(root, "test.plugin.data");
        progressQueue = new MainThreadProgressQueue(new NullPluginLog());
        return new PluginDownloadService(dataDir, new HttpClient(handler), new FakeResume(), new NullPluginLog(), progressQueue, inactivityTimeout);
    }

    private static DownloadRequest Req(byte[] payload, string folder, bool zip, IReadOnlyList<string>? prefixes = null,
        string? sha = null, long maxBytes = 10_000_000) =>
        new(new Uri("https://cdn.example.com/pack.bin"), sha ?? Hex(payload), maxBytes, folder, zip, prefixes);

    private static string Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static byte[] BuildZip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var es = archive.CreateEntry(name).Open();
                es.Write(content, 0, content.Length);
            }
        }
        return ms.ToArray();
    }

    // ── Test doubles ──

    private sealed class FakeResume : IMainThreadResume
    {
        public int Calls;
        public Task ResumeOnMainThreadAsync() { Calls++; return Task.CompletedTask; }
    }

    /// <summary>Returns queued responses in order; records every requested URL. Never dispatches when the
    /// service rejects a request before reaching HTTP (https-only / folder rules) — tests assert on Requested.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly List<Uri> Requested = new();
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public void Enqueue(Func<HttpResponseMessage> make) => _responses.Enqueue(make);
        public void EnqueueBytes(byte[] bytes) => Enqueue(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requested.Add(request.RequestUri!);
            if (_responses.Count == 0) throw new InvalidOperationException("no fake response queued");
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    /// <summary>Blocks every request on a gate the test controls (or forever, if never set), so a download can
    /// be kept "in flight" or made to stall. Honours the caller's token — a real cancellation (or our own
    /// inactivity timeout, which cancels the SAME linked token) unblocks it instead of leaking a pending task.</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<HttpResponseMessage> Gate = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private sealed class CollectProgress : IProgress<double>
    {
        public readonly List<double> Values = new();
        public void Report(double value) => Values.Add(value);
    }

    // ── Gates ──

    [Fact]
    public async Task Https_only_rejects_plain_http_without_dispatching()
    {
        var handler = new FakeHandler();
        var svc = NewService(handler, out _);
        var req = new DownloadRequest(new Uri("http://cdn.example.com/pack.bin"), "deadbeef", 1024, "pack", false);

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("https only", result.Error);
        Assert.Empty(handler.Requested);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/etc/passwd")]
    public async Task Folder_rules_reject_unsafe_target_folders_without_dispatching(string unsafeFolder)
    {
        var handler = new FakeHandler();
        var svc = NewService(handler, out _);
        var req = Req(new byte[] { 1 }, unsafeFolder, zip: false);

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("folder not allowed", result.Error);
        Assert.Empty(handler.Requested);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Non_positive_max_bytes_is_rejected_without_dispatching(long maxBytes)
    {
        var handler = new FakeHandler();
        var svc = NewService(handler, out _);
        var payload = new byte[] { 1 };
        var req = new DownloadRequest(new Uri("https://cdn.example.com/pack.bin"), Hex(payload), maxBytes, "pack/file.bin", false);

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("invalid size", result.Error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task Max_bytes_above_the_array_length_ceiling_is_rejected()
    {
        var handler = new FakeHandler();
        var svc = NewService(handler, out _);
        var payload = new byte[] { 1 };
        var req = new DownloadRequest(new Uri("https://cdn.example.com/pack.bin"), Hex(payload), (long)Array.MaxLength + 1, "pack/file.bin", false);

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("invalid size", result.Error);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task Checksum_mismatch_writes_nothing()
    {
        var payload = new byte[] { 1, 2, 3, 4 };
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var svc = NewService(handler, out var dataDir);
        var req = Req(payload, "pack/file.bin", zip: false, sha: "0000000000000000000000000000000000000000000000000000000000000000");

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("checksum mismatch", result.Error);
        Assert.False(Directory.Exists(dataDir), "nothing should be written on a checksum mismatch");
    }

    [Fact]
    public async Task A_non_2xx_response_maps_to_network_error()
    {
        var handler = new FakeHandler();
        handler.Enqueue(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var svc = NewService(handler, out _);

        var result = await svc.DownloadAsync(Req(new byte[] { 1 }, "pack/file.bin", zip: false), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("network error", result.Error);
    }

    [Fact]
    public async Task Zip_extraction_honours_include_prefixes()
    {
        var zip = BuildZip(("shaders/Bloom.fx", new byte[] { 1 }), ("textures/noise.png", new byte[] { 2 }));
        var handler = new FakeHandler();
        handler.EnqueueBytes(zip);
        var svc = NewService(handler, out var dataDir);
        var req = Req(zip, "reshade/packs/standard", zip: true, prefixes: new[] { "shaders" });

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        var root = Path.Combine(dataDir, "reshade", "packs", "standard");
        Assert.True(File.Exists(Path.Combine(root, "shaders", "Bloom.fx")));
        Assert.False(File.Exists(Path.Combine(root, "textures", "noise.png")));
    }

    [Fact]
    public async Task A_second_zip_replaces_the_previous_content_rather_than_merging_with_it()
    {
        var first = BuildZip(("a.fx", new byte[] { 1 }));
        var second = BuildZip(("b.fx", new byte[] { 2 }));
        var handler = new FakeHandler();
        handler.EnqueueBytes(first);
        handler.EnqueueBytes(second);
        var svc = NewService(handler, out var dataDir);
        var folder = "reshade/packs/standard";

        var r1 = await svc.DownloadAsync(Req(first, folder, zip: true), null, CancellationToken.None);
        Assert.True(r1.Ok, r1.Error);
        var root = Path.Combine(dataDir, "reshade", "packs", "standard");
        Assert.True(File.Exists(Path.Combine(root, "a.fx")));

        var r2 = await svc.DownloadAsync(Req(second, folder, zip: true), null, CancellationToken.None);
        Assert.True(r2.Ok, r2.Error);
        Assert.False(File.Exists(Path.Combine(root, "a.fx")), "stale content from the previous pack must not survive");
        Assert.True(File.Exists(Path.Combine(root, "b.fx")));

        // No leftover temp/backup siblings from the atomic swap.
        var siblings = Directory.GetFileSystemEntries(Path.GetDirectoryName(root)!);
        Assert.Equal(new[] { root }, siblings);
    }

    [Fact]
    public async Task A_failed_zip_swap_leaves_no_temp_or_backup_siblings_and_preserves_old_content()
    {
        var zip = BuildZip(("a.fx", new byte[] { 1 }));
        var handler = new FakeHandler();
        handler.EnqueueBytes(zip);
        var svc = NewService(handler, out var dataDir);
        var targetParent = Path.Combine(dataDir, "reshade", "packs");
        Directory.CreateDirectory(targetParent);
        // Occupy the swap target with a plain FILE — Directory.Move(temp, target) can never land on it,
        // simulating an unwritable/occupied target without needing OS-level permission tricks.
        File.WriteAllText(Path.Combine(targetParent, "standard"), "not a directory");

        var result = await svc.DownloadAsync(Req(zip, "reshade/packs/standard", zip: true), null, CancellationToken.None);

        Assert.False(result.Ok);
        var siblings = Directory.GetFileSystemEntries(targetParent);
        Assert.Equal(new[] { Path.Combine(targetParent, "standard") }, siblings);
        Assert.Equal("not a directory", File.ReadAllText(Path.Combine(targetParent, "standard")));
    }

    [Fact]
    public async Task A_failed_plain_file_write_leaves_no_download_tmp_behind_and_preserves_old_content()
    {
        var payload = new byte[] { 1, 2, 3 };
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var svc = NewService(handler, out var dataDir);
        var targetParent = Path.Combine(dataDir, "pack");
        // "file.bin" is itself a pre-existing directory — File.Move can never overwrite it with a file.
        Directory.CreateDirectory(Path.Combine(targetParent, "file.bin"));

        var result = await svc.DownloadAsync(Req(payload, "pack/file.bin", zip: false), null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.True(Directory.Exists(Path.Combine(targetParent, "file.bin")), "the old content must survive a failed write");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(targetParent), e => e.EndsWith(".download-tmp"));
    }

    [Fact]
    public void SwapDirectory_restores_the_backup_when_the_second_move_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "stellar-dl-" + Path.GetRandomFileName());
        _tempRoots.Add(root);
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "pack");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.txt"), "old");
        var missingTemp = Path.Combine(root, "pack.new-missing"); // never created — forces the second move to fail

        var svc = NewService(new FakeHandler(), out _);
        Assert.ThrowsAny<IOException>(() => svc.SwapDirectory(missingTemp, target));

        Assert.True(Directory.Exists(target), "the original content must be restored, not left aside as a backup");
        Assert.True(File.Exists(Path.Combine(target, "old.txt")));
        Assert.DoesNotContain(Directory.GetFileSystemEntries(root), e => e.Contains(".old-"));
    }

    [Fact]
    public async Task A_plain_file_download_overwrites_an_existing_file()
    {
        var handler = new FakeHandler();
        var newPayload = new byte[] { 9, 8, 7 };
        handler.EnqueueBytes(newPayload);
        var svc = NewService(handler, out var dataDir);
        var path = Path.Combine(dataDir, "pack", "file.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });

        var result = await svc.DownloadAsync(Req(newPayload, "pack/file.bin", zip: false), null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(newPayload, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Second_call_while_one_is_in_flight_returns_busy_without_waiting()
    {
        var handler = new BlockingHandler();
        var svc = NewService(handler, out _);
        var req = Req(new byte[] { 1, 2, 3 }, "pack/file.bin", zip: false);

        var first = svc.DownloadAsync(req, null, CancellationToken.None);
        var second = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(second.Ok);
        Assert.Equal("busy", second.Error);

        handler.Gate.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) });
        var completedFirst = await first;
        Assert.True(completedFirst.Ok, completedFirst.Error);
    }

    [Fact]
    public async Task A_stalled_download_times_out_and_releases_the_gate()
    {
        var handler = new BlockingHandler(); // Gate is never set — the request stalls until the inactivity timeout fires
        var svc = NewService(handler, out _, out _, inactivityTimeout: TimeSpan.FromMilliseconds(30));
        var req = Req(new byte[] { 1 }, "pack/file.bin", zip: false);

        var first = await svc.DownloadAsync(req, null, CancellationToken.None);
        Assert.False(first.Ok);
        Assert.Equal("timed out", first.Error);

        // A fresh call must not see "busy" — the gate was released after the timeout.
        var second = await svc.DownloadAsync(req, null, CancellationToken.None);
        Assert.False(second.Ok);
        Assert.NotEqual("busy", second.Error);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_releases_the_gate()
    {
        var handler = new BlockingHandler();
        var svc = NewService(handler, out _, out _, inactivityTimeout: TimeSpan.FromSeconds(30));
        var req = Req(new byte[] { 1 }, "pack/file.bin", zip: false);

        using (var cts = new CancellationTokenSource())
        {
            var first = svc.DownloadAsync(req, null, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        }

        // A fresh call must not see "busy" — proven by it reaching the (also cancelled) HTTP call rather
        // than returning a normal "busy" DownloadResult.
        using var cts2 = new CancellationTokenSource();
        var second = svc.DownloadAsync(req, null, cts2.Token);
        cts2.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
    }

    [Fact]
    public async Task Streaming_cap_applies_even_without_a_content_length_header()
    {
        var payload = new byte[1000];
        new Random(2).NextBytes(payload);
        var handler = new FakeHandler();
        handler.Enqueue(() =>
        {
            var content = new StreamContent(new MemoryStream(payload));
            content.Headers.ContentLength = null; // simulate a chunked response with no declared length
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        var svc = NewService(handler, out _);
        var req = Req(payload, "pack/file.bin", zip: false, maxBytes: 500);

        var result = await svc.DownloadAsync(req, null, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("too large", result.Error);
    }

    // Fix round 2 — N2: a failed download must drop any pending progress rather than let it surface on a
    // later drain, after DownloadAsync has already returned the failure to the caller.
    [Fact]
    public async Task A_failed_download_drops_any_pending_progress_so_nothing_arrives_after_completion()
    {
        var payload = new byte[200_000]; // bigger than one 81920-byte read chunk, so Report fires mid-stream
        new Random(3).NextBytes(payload);
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var svc = NewService(handler, out _, out var queue);
        var progress = new CollectProgress();
        // A wrong checksum — real intermediate progress is reported before the mismatch is even checked.
        var req = Req(payload, "pack/file.bin", zip: false, sha: new string('0', 64));

        var result = await svc.DownloadAsync(req, progress, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.False(queue.HasQueued, "a failed download must not leave a pending progress value for a later drain");
        Assert.Equal(0, queue.Drain());
        Assert.Empty(progress.Values); // nothing was ever drained, so nothing was ever delivered
    }

    [Fact]
    public async Task Progress_is_delivered_only_through_the_main_thread_drain()
    {
        var payload = new byte[50_000];
        new Random(1).NextBytes(payload);
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var svc = NewService(handler, out var dataDir, out var queue);
        var progress = new CollectProgress();
        var req = Req(payload, "reshade/ReShade64.dll", zip: false);

        var result = await svc.DownloadAsync(req, progress, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Empty(progress.Values);    // never delivered directly from the worker
        Assert.True(queue.HasQueued);

        var delivered = queue.Drain();

        Assert.True(delivered > 0);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.False(queue.HasQueued);
        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dataDir, "reshade", "ReShade64.dll")));
    }

    [Fact]
    public async Task Successful_download_resumes_on_the_main_thread_exactly_once()
    {
        var payload = new byte[] { 9, 9, 9 };
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var root = Path.Combine(Path.GetTempPath(), "stellar-dl-" + Path.GetRandomFileName());
        _tempRoots.Add(root);
        var dataDir = Path.Combine(root, "test.plugin.data");
        var resume = new FakeResume();
        var svc = new PluginDownloadService(dataDir, new HttpClient(handler), resume, new NullPluginLog(), new MainThreadProgressQueue(new NullPluginLog()));

        var result = await svc.DownloadAsync(Req(payload, "pack/file.bin", zip: false), null, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(1, resume.Calls);
    }

    [Fact]
    public void Data_folder_is_the_path_passed_in()
    {
        var svc = NewService(new FakeHandler(), out var dataDir);
        Assert.Equal(dataDir, svc.DataFolder);
    }
}
