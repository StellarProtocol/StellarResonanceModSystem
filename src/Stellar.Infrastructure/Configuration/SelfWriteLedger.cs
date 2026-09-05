// src/Stellar.Infrastructure/Configuration/SelfWriteLedger.cs
using System;
using System.Collections.Concurrent;

namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// Echo ledger for <see cref="FileConfigStore"/>: remembers the writes WE performed so the
/// FileSystemWatcher notifications they provoke (Wine/Windows deliver 2-4 per
/// <c>File.WriteAllText</c>) are recognised as our own instead of being reported as external edits.
///
/// <para>Two recognition paths, cheapest first:</para>
/// <list type="number">
/// <item><b>Stat</b> — the <c>(byte length, last-write time)</c> of the completed write. One
/// <c>FileInfo</c> answers it, so recognising an echo costs no read and no hash. This is the path
/// that matters for the owner's 2026-09-05 save-click report: re-reading + SHA256-ing a 106 KB
/// config for EVERY watcher event put ~840 KB/save on the large-object heap, forcing a gen2 GC
/// (a 100-475 ms frame freeze on this client) every ~7 saves.</item>
/// <item><b>Content hash</b> — the fallback, recorded BEFORE the write starts, so an event
/// delivered while <c>File.WriteAllText</c> is still running (i.e. before the stat is knowable) is
/// still recognised. Costs a read + SHA256, exactly as the whole path did before the stat filter.</item>
/// </list>
///
/// <para>Records are PEEKED, never consumed: a single write raises several events carrying the same
/// content, and consuming on the first let all the rest fall through as bogus "external edits" —
/// each triggering a full config reload + reparse on the game thread. A TTL bounds how long a
/// record may suppress; a genuine external edit differs in stat AND in hash, so it falls through
/// both paths regardless.</para>
///
/// <para>The stat map holds only the LATEST completed write per plugin, which is sufficient and
/// correct: <see cref="FileConfigStore"/> stats the file at event-handling time, so what it sees is
/// always the newest write's state — a late event from an older write matches the newer record.</para>
///
/// <para>The stat path gets a SHORTER TTL than the hash path on purpose. It rests on one assumption
/// — that a genuine external edit lands on a different mtime tick than our write — which only fails
/// on a filesystem with coarse mtime granularity, and then only for an edit of the exact same byte
/// length inside the same tick. Watcher echoes arrive within milliseconds (on the watcher's own
/// thread, so a stalled game thread does not delay them), so keeping the stat record alive for
/// seconds buys nothing and only widens that window. Letting it expire early costs a read, never
/// correctness: the hash path still recognises the write for the full TTL.</para>
/// </summary>
internal sealed class SelfWriteLedger
{
    private readonly TimeSpan _hashTtl;
    private readonly TimeSpan _statTtl;

    // "guid|hash" -> when recorded. Written before the file write, so it covers events that race it.
    private readonly ConcurrentDictionary<string, DateTime> _hashes = new(StringComparer.Ordinal);

    // guid -> stat of that plugin's most recent completed write.
    private readonly ConcurrentDictionary<string, StatRecord> _stats = new(StringComparer.Ordinal);

    internal SelfWriteLedger(TimeSpan hashTtl, TimeSpan statTtl)
    {
        _hashTtl = hashTtl;
        _statTtl = statTtl;
    }

    /// <summary>Records the content hash of a write about to be performed, and drops records that
    /// have outlived the TTL.</summary>
    internal void RecordPending(string pluginGuid, string contentHash, DateTime nowUtc)
    {
        PruneExpired(nowUtc);
        _hashes[BuildHashKey(pluginGuid, contentHash)] = nowUtc;
    }

    /// <summary>Records the on-disk stat of a completed write — the cheap key
    /// <see cref="IsStatEcho"/> matches watcher events against.</summary>
    internal void RecordCompleted(string pluginGuid, long length, DateTime lastWriteUtc, DateTime nowUtc)
        => _stats[pluginGuid] = new StatRecord(nowUtc, length, lastWriteUtc);

    /// <summary>
    /// True when <paramref name="length"/> and <paramref name="lastWriteUtc"/> match this plugin's
    /// live write record — i.e. the touch is the echo of our own write and the file need not be
    /// read. A same-length external edit has a later write time, so it does NOT match.
    /// </summary>
    internal bool IsStatEcho(string pluginGuid, long length, DateTime lastWriteUtc, DateTime nowUtc)
        => _stats.TryGetValue(pluginGuid, out var rec)
           && !IsExpired(rec.RecordedUtc, nowUtc, _statTtl)
           && rec.Length == length
           && rec.LastWriteUtc == lastWriteUtc;

    /// <summary>Fallback recognition by content hash, for events that arrive before the stat of the
    /// write they belong to was recorded (or after the short stat TTL lapsed).</summary>
    internal bool IsHashEcho(string pluginGuid, string contentHash, DateTime nowUtc)
        => _hashes.TryGetValue(BuildHashKey(pluginGuid, contentHash), out var recordedUtc)
           && !IsExpired(recordedUtc, nowUtc, _hashTtl);

    private static bool IsExpired(DateTime recordedUtc, DateTime nowUtc, TimeSpan ttl) => nowUtc - recordedUtc > ttl;

    private void PruneExpired(DateTime nowUtc)
    {
        foreach (var kv in _hashes)
        {
            if (IsExpired(kv.Value, nowUtc, _hashTtl)) _hashes.TryRemove(kv.Key, out _);
        }
        foreach (var kv in _stats)
        {
            if (IsExpired(kv.Value.RecordedUtc, nowUtc, _statTtl)) _stats.TryRemove(kv.Key, out _);
        }
    }

    private static string BuildHashKey(string pluginGuid, string contentHash) => pluginGuid + "|" + contentHash;

    private readonly struct StatRecord
    {
        internal StatRecord(DateTime recordedUtc, long length, DateTime lastWriteUtc)
        {
            RecordedUtc = recordedUtc;
            Length = length;
            LastWriteUtc = lastWriteUtc;
        }

        internal DateTime RecordedUtc { get; }
        internal long Length { get; }
        internal DateTime LastWriteUtc { get; }
    }
}
