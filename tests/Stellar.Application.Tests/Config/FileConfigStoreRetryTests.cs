// tests/Stellar.Application.Tests/Config/FileConfigStoreRetryTests.cs
//
// Pins for the 2026-10-23 dropped-save bug: MultiTool saved twice back-to-back and the second
// background write hit "The process cannot access the file ... being used by another process"
// (an EXTERNAL transient lock — Defender scanning the just-written file, or a watcher read). The
// writer logged and DROPPED it, so the on-disk config was stale until the next Save. The writer now
// retries an IOException with a short backoff, abandoning the attempt if a newer Save superseded it.
//
// Hermetic: a temp directory and a store whose writer thread is stopped (Dispose), so the write is
// driven synchronously by FlushPendingWritesForTest on the test thread.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests.Config;

public sealed class FileConfigStoreRetryTests : IDisposable
{
    private const string Guid = "stellar.multitool";
    private const string FileName = Guid + ".config.json";

    private readonly string _dir;
    private readonly RecordingLog _log = new();

    public FileConfigStoreRetryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stellar-cfgretry-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_ => Path.Combine(_dir, FileName);

    private FileConfigStore NewSilentStore()
    {
        var store = new FileConfigStore(_log, _dir);
        store.Dispose();   // detach the watcher + stop the writer thread; Save stays usable
        return store;
    }

    private static JsonObject Root(int value) => new() { ["invite"] = new JsonObject { ["count"] = value } };

    // An exclusive handle stands in for Defender / a watcher read holding the just-written file.
    private FileStream LockTarget() => new(Path_, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

    [Fact]
    public void WriteBlockedByTransientLock_IsRetried_AndLands()
    {
        var store = NewSilentStore();
        var holder = LockTarget();
        store.Save(Guid, Root(1));

        var releaser = new Thread(() => { Thread.Sleep(150); holder.Dispose(); });
        releaser.Start();
        store.FlushPendingWritesForTest();   // real backoff: 50, 100, 200 ms ... outlasts the 150 ms lock
        releaser.Join();

        Assert.Equal("""{"invite":{"count":1}}""", File.ReadAllText(Path_));
        Assert.Empty(_log.Warnings);
        Assert.Contains(_log.Infos, m => m.Contains("succeeded after"));
    }

    [Fact]
    public void NewerSaveDuringBackoff_SupersedesTheBlockedWrite()
    {
        var store = NewSilentStore();
        using (LockTarget())
        {
            store.Save(Guid, Root(1));
            // Deterministic "a newer Save landed while we were backing off".
            store.RetrySleepForTest = _ => store.Save(Guid, Root(2));
            store.FlushPendingWritesForTest();   // must abandon write 1 at once — not exhaust retries
            store.RetrySleepForTest = _ => { };
            Assert.Empty(_log.Warnings);
        }

        store.FlushPendingWritesForTest();   // the newer write is still queued and now lands

        Assert.Equal("""{"invite":{"count":2}}""", File.ReadAllText(Path_));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void LockThatNeverClears_WarnsOnceWithTheAttemptCount()
    {
        var store = NewSilentStore();
        store.RetrySleepForTest = _ => { };   // skip the real ~1.5 s of backoff
        using (LockTarget())
        {
            store.Save(Guid, Root(1));
            store.FlushPendingWritesForTest();
        }

        var warning = Assert.Single(_log.Warnings);
        Assert.Contains("after 6 attempt(s)", warning);
    }

    private sealed class RecordingLog : IPluginLog
    {
        internal readonly List<string> Infos = new();
        internal readonly List<string> Warnings = new();
        public void Info(string message) { lock (Infos) Infos.Add(message); }
        public void Warning(string message) { lock (Warnings) Warnings.Add(message); }
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
