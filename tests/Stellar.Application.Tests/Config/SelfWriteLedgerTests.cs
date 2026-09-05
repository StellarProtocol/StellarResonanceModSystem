// tests/Stellar.Application.Tests/Config/SelfWriteLedgerTests.cs
//
// Regression pins for the 2026-09-05 save-click spike (owner report: every click of a plugin's save
// button froze a frame for 287-366 ms, then stuttered for seconds). Root cause: FileConfigStore read
// + SHA256-hashed the whole config for EVERY FileSystemWatcher event just to recognise its OWN write
// — a 106 KB LOH string plus a 54 KB byte[] per event, 2-4 events per save.
//
// These pin the pure echo DECISION (no disk, no watcher): the cheap stat path recognises our own
// write, and — critically — does NOT mistake a real external edit for one.
using System;
using Stellar.Infrastructure.Configuration;
using Xunit;

namespace Stellar.Application.Tests.Config;

public sealed class SelfWriteLedgerTests
{
    private const string Guid = "stellar.wardrobeloadout";
    private const string OtherGuid = "stellar.combatmeter";
    private static readonly DateTime T0 = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime WriteTime = new(2026, 9, 5, 11, 59, 59, DateTimeKind.Utc);

    // Mirrors FileConfigStore's real TTLs: 5s for the hash path, 1s for the stat path.
    private static readonly TimeSpan HashTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatTtl = TimeSpan.FromSeconds(1);

    private static SelfWriteLedger NewLedger() => new(HashTtl, StatTtl);

    private static SelfWriteLedger AfterOurWrite(long length = 20_762, string hash = "h1")
    {
        var ledger = NewLedger();
        ledger.RecordPending(Guid, hash, T0);
        ledger.RecordCompleted(Guid, length, WriteTime, T0);
        return ledger;
    }

    [Fact]
    public void StatMatchingOurWrite_IsEcho()
    {
        Assert.True(AfterOurWrite().IsStatEcho(Guid, 20_762, WriteTime, T0));
    }

    // Pin (c) of the fix brief: length alone must never decide. An external editor that happens to
    // write the same number of bytes still carries a later mtime and MUST fall through to the hash
    // path, or a real user edit would be silently swallowed.
    [Fact]
    public void SameLengthButLaterWriteTime_IsNotEcho()
    {
        Assert.False(AfterOurWrite().IsStatEcho(Guid, 20_762, WriteTime.AddSeconds(1), T0));
    }

    [Fact]
    public void SameWriteTimeButDifferentLength_IsNotEcho()
    {
        Assert.False(AfterOurWrite().IsStatEcho(Guid, 20_763, WriteTime, T0));
    }

    [Fact]
    public void NoRecordAtAll_IsNotEcho()
    {
        Assert.False(NewLedger().IsStatEcho(Guid, 20_762, WriteTime, T0));
    }

    // The stat path expires FAST on purpose: it assumes a real external edit lands on a different
    // mtime tick, and a watcher echo always arrives within milliseconds, so there is nothing to gain
    // from keeping the record alive for seconds. Expiring early costs a read, never correctness —
    // the hash path below still recognises the same write for the full 5s.
    [Fact]
    public void StatRecordOlderThanTheStatTtl_IsNotEcho()
    {
        var ledger = AfterOurWrite();
        var afterStatTtl = T0 + StatTtl + TimeSpan.FromMilliseconds(1);
        Assert.False(ledger.IsStatEcho(Guid, 20_762, WriteTime, afterStatTtl));
        Assert.True(ledger.IsHashEcho(Guid, "h1", afterStatTtl));
    }

    [Fact]
    public void StatRecordInsideTheStatTtl_IsStillEcho()
    {
        Assert.True(AfterOurWrite().IsStatEcho(Guid, 20_762, WriteTime, T0.AddMilliseconds(900)));
    }

    [Fact]
    public void AnotherPluginsWrite_IsNotEchoForThisPlugin()
    {
        Assert.False(AfterOurWrite().IsStatEcho(OtherGuid, 20_762, WriteTime, T0));
    }

    // One File.WriteAllText raises 2-4 watcher events. The record is PEEKED, never consumed — the
    // pre-fix code consumed on the first event, so every later event for the SAME write fell through
    // and was misclassified as an external edit (full reload + reparse on the game thread each time).
    [Fact]
    public void RepeatedTouchesFromOneWrite_AreAllSuppressed()
    {
        var ledger = AfterOurWrite();
        for (var i = 0; i < 4; i++)
        {
            Assert.True(ledger.IsStatEcho(Guid, 20_762, WriteTime, T0), $"touch {i} should still be an echo");
        }
    }

    // The hash path is the fallback for an event delivered while our File.WriteAllText was still
    // running: the stat of that write is not recorded yet, so only the pre-write hash can catch it.
    [Fact]
    public void HashEcho_RecognisesWriteWhoseStatIsNotRecordedYet()
    {
        var ledger = NewLedger();
        ledger.RecordPending(Guid, "h1", T0);   // no RecordCompleted — write still in flight
        Assert.False(ledger.IsStatEcho(Guid, 20_762, WriteTime, T0));
        Assert.True(ledger.IsHashEcho(Guid, "h1", T0));
    }

    [Fact]
    public void HashEcho_DifferentContent_IsNotEcho()
    {
        Assert.False(AfterOurWrite().IsHashEcho(Guid, "someone-elses-content", T0));
    }

    [Fact]
    public void HashEcho_OlderThanTtl_IsNotEcho()
    {
        Assert.False(AfterOurWrite().IsHashEcho(Guid, "h1", T0.AddSeconds(6)));
    }

    // A later save must not be shadowed by an earlier one: the ledger keeps the LATEST completed
    // write's stat per plugin, which is what HandleFileTouch sees when it stats the file.
    [Fact]
    public void SecondSave_ReplacesTheStatOfTheFirst()
    {
        var ledger = AfterOurWrite();
        var secondWriteTime = WriteTime.AddMilliseconds(400);
        ledger.RecordPending(Guid, "h2", T0);
        ledger.RecordCompleted(Guid, 20_900, secondWriteTime, T0);

        Assert.True(ledger.IsStatEcho(Guid, 20_900, secondWriteTime, T0));
        Assert.False(ledger.IsStatEcho(Guid, 20_762, WriteTime, T0));
        // Both hashes stay live inside the TTL, so a late event from the first write is still ours.
        Assert.True(ledger.IsHashEcho(Guid, "h1", T0));
        Assert.True(ledger.IsHashEcho(Guid, "h2", T0));
    }
}
