// tests/Stellar.Application.Tests/Config/FileConfigStoreEchoTests.cs
//
// Regression pins for the 2026-09-05 save-click spike (owner report: clicking a plugin's save button
// — Wardrobe "Save current outfit", 53 outfits, a 57 KB config — froze a frame for 287-366 ms and
// then stuttered for seconds). Measured cause: one Set+Save allocated 1,167 KB, ~420 KB of it on the
// large-object heap, forcing a full gen2 GC every ~7 saves (gen2 pauses on this client are
// 100-475 ms). 72 % of that came from FileConfigStore re-reading and SHA256-ing the whole config for
// EVERY FileSystemWatcher event, before it ever checked whether the write was its own.
//
// Hermetic: a temp directory, and HandleFileTouch driven directly, so nothing here races inotify.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests.Config;

public sealed class FileConfigStoreEchoTests : IDisposable
{
    private const string Guid = "stellar.wardrobeloadout";
    private const string FileName = Guid + ".config.json";

    private readonly string _dir;
    private readonly List<string> _fired = new();
    private int _reads;

    public FileConfigStoreEchoTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stellar-cfgstore-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_ => Path.Combine(_dir, FileName);

    /// <summary>
    /// A store whose live FileSystemWatcher is detached (Dispose only tears the watcher down; Save /
    /// TryLoad / HandleFileTouch stay fully usable), with a counting content reader so a test can
    /// assert how many times the watcher path actually READ the file.
    /// </summary>
    private FileConfigStore NewSilentStore()
    {
        var store = new FileConfigStore(new StubLog(), _dir, path => { _reads++; return File.ReadAllText(path); });
        store.Dispose();
        store.ExternalFileChanged += guid => _fired.Add(guid);
        return store;
    }

    private static JsonObject Root(int value) => new() { ["wardrobe"] = new JsonObject { ["outfits"] = value } };

    private void Touch(FileConfigStore store)
    {
        store.HandleFileTouch(Path_, FileName);
        store.DrainExternalEvents();
    }

    /// <summary>Rewrites the file behind the store's back, with an mtime the store cannot have recorded.</summary>
    private void ExternalWrite(string content)
    {
        File.WriteAllText(Path_, content);
        File.SetLastWriteTimeUtc(Path_, File.GetLastWriteTimeUtc(Path_).AddSeconds(1));
    }

    // Pin (a): the echo of our own write is recognised from the file's stat alone — ZERO reads, zero
    // hashing. This is the allocation the owner felt as a frame freeze.
    [Fact]
    public void SelfWriteEcho_IsSuppressedWithoutReadingTheFile()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));

        Touch(store);

        Assert.Equal(0, _reads);
        Assert.Empty(_fired);
    }

    // One File.WriteAllText raises 2-4 watcher events (NotifyFilter spans LastWrite|Size, and
    // Wine/Windows deliver several). ALL of them must be recognised, and none may read.
    [Fact]
    public void EveryWatcherEventFromOneSave_IsSuppressedWithoutReading()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));

        for (var i = 0; i < 4; i++) Touch(store);

        Assert.Equal(0, _reads);
        Assert.Empty(_fired);
    }

    // Pin (b): a genuine external edit still falls through to the read+hash path and still fires.
    [Fact]
    public void ExternalEdit_StillFallsThroughToTheHashPathAndFires()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));
        ExternalWrite("""{"wardrobe":{"outfits":2}}""");

        Touch(store);

        Assert.Equal(1, _reads);                 // the stat did not match, so we paid for the read
        Assert.Equal(new[] { Guid }, _fired);
    }

    // Pin (c): a same-LENGTH external edit is not mistaken for our write — length alone never decides.
    [Fact]
    public void ExternalEdit_OfIdenticalLength_IsStillDetected()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));
        var ours = File.ReadAllText(Path_);
        ExternalWrite(ours.Replace("1", "2"));   // same char count, different content + later mtime
        Assert.Equal(ours.Length, File.ReadAllText(Path_).Length);

        Touch(store);

        Assert.Equal(new[] { Guid }, _fired);
    }

    // The hash fallback stays live: an external tool that rewrites byte-identical content within the
    // TTL is still recognised as our own (unchanged behaviour — it carries no new state to react to).
    [Fact]
    public void ExternalRewriteOfIdenticalContent_IsSuppressedByTheHashFallback()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));
        ExternalWrite(File.ReadAllText(Path_));

        Touch(store);

        Assert.Equal(1, _reads);                 // stat differs → we read, then the hash recognises it
        Assert.Empty(_fired);
    }

    // A touch for a file that never existed must not throw out of the watcher thread, and must not fire.
    [Fact]
    public void TouchForMissingFile_IsIgnored()
    {
        var store = NewSilentStore();

        Touch(store);

        Assert.Empty(_fired);
    }

    // Fix (3): saves are COMPACT. Indenting the owner's nested config inflated it 20 K -> 55 K chars,
    // i.e. a >85 KB string on the large-object heap on every single save.
    [Fact]
    public void Save_WritesCompactJson_NotIndented()
    {
        var store = NewSilentStore();
        store.Save(Guid, Root(1));

        var text = File.ReadAllText(Path_);
        Assert.DoesNotContain("\n", text);
        Assert.Equal("""{"wardrobe":{"outfits":1}}""", text);
    }

    // Rollback safety (process rules § 6): a config written by an older, indent-writing build must
    // still load byte-for-byte identically after this change.
    [Fact]
    public void Load_StillAcceptsAnIndentedFileFromAnOlderBuild()
    {
        File.WriteAllText(Path_, "{\n  \"wardrobe\": {\n    \"outfits\": 7\n  }\n}");
        var store = NewSilentStore();

        Assert.True(store.TryLoad(Guid, out var root));
        Assert.Equal(7, root!["wardrobe"]!["outfits"]!.GetValue<int>());
    }

    // The stat path is the one part of the echo decision that makes an assumption about the
    // filesystem (that a real external edit lands on a different mtime tick than our write). It is
    // therefore deliberately short-lived, far shorter than the hash path it degrades into. Pinned so
    // widening it back is a conscious, reviewed act — see SelfWriteLedger for the reasoning.
    [Fact]
    public void StatEchoWindow_IsMuchShorterThanTheHashEchoWindow()
    {
        Assert.True(FileConfigStore.SelfWriteStatTtl < FileConfigStore.SelfWriteTtl,
            $"stat TTL {FileConfigStore.SelfWriteStatTtl} must stay below hash TTL {FileConfigStore.SelfWriteTtl}");
        Assert.True(FileConfigStore.SelfWriteStatTtl <= TimeSpan.FromSeconds(1));
    }

    private sealed class StubLog : IPluginLog
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
        public void Debug(string message) { }
    }
}
