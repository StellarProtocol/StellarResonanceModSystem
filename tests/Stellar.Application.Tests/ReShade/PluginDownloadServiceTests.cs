using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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

    private PluginDownloadService NewService(HttpMessageHandler handler, out string dataDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "stellar-dl-" + Path.GetRandomFileName());
        _tempRoots.Add(root);
        dataDir = Path.Combine(root, "test.plugin.data");
        return new PluginDownloadService(dataDir, new HttpClient(handler), new FakeResume(), new NullPluginLog());
    }

    private static DownloadRequest Req(byte[] payload, string folder, bool zip, IReadOnlyList<string>? prefixes = null, string? sha = null) =>
        new(new Uri("https://cdn.example.com/pack.bin"), sha ?? Hex(payload), 10_000_000, folder, zip, prefixes);

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

    /// <summary>Blocks every request on a gate the test controls, so a download can be kept "in flight".</summary>
    private sealed class BlockingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource<HttpResponseMessage> Gate = new();
        public int Dispatches;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Dispatches);
            return await Gate.Task.ConfigureAwait(false);
        }
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
    public async Task Progress_reaches_one_on_a_successful_plain_file_download()
    {
        var payload = new byte[50_000];
        new Random(1).NextBytes(payload);
        var handler = new FakeHandler();
        handler.EnqueueBytes(payload);
        var svc = NewService(handler, out var dataDir);
        var progress = new CollectProgress();
        var req = Req(payload, "reshade/ReShade64.dll", zip: false);

        var result = await svc.DownloadAsync(req, progress, CancellationToken.None);

        Assert.True(result.Ok, result.Error);
        Assert.NotEmpty(progress.Values);
        Assert.Equal(1.0, progress.Values[^1]);
        Assert.True(File.Exists(Path.Combine(dataDir, "reshade", "ReShade64.dll")));
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
        var svc = new PluginDownloadService(dataDir, new HttpClient(handler), resume, new NullPluginLog());

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
