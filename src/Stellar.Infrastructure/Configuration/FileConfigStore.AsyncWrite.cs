// src/Stellar.Infrastructure/Configuration/FileConfigStore.AsyncWrite.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// Background-writer half of <see cref="FileConfigStore"/>. The actual
/// <see cref="File.WriteAllText(string,string)"/> runs on a single dedicated thread, NEVER on the
/// game thread.
///
/// <para>Why: a synchronous config write on the game thread can stall it for SECONDS. The first
/// save after launch measured a 3.4 s frame freeze (<c>[Hitch] frame=3396ms cfg-save=3378ms</c> in
/// <c>svc:window</c> on a layout drag) — a first-touch cost (Windows Defender real-time scan of the
/// file) that no amount of serialize/hash tuning removes, because it is the disk write itself. Every
/// later save was ~1.4 ms, but the game thread can never afford the worst case. See
/// <c>Knowledge Base/Config-Store-Async-Writes.md</c>.</para>
///
/// <para>The game thread keeps only the cheap work (serialize + hash + <c>RecordPending</c>) and
/// hands <c>(guid, path, serialized)</c> off here. The worker only ever sees the immutable
/// <c>serialized</c> string — the <see cref="System.Text.Json.Nodes.JsonNode"/> root is read solely
/// on the game thread, so no JsonNode ever crosses the thread boundary.</para>
///
/// <para>Per-path coalescing (last-write-wins): rapid saves of the same config (e.g. a window drag
/// persisting its rect every frame) collapse to a single write of the newest content. This is
/// correct because configs are read only at load and by the watcher — never re-read synchronously
/// after a Save in-process.</para>
/// </summary>
internal sealed partial class FileConfigStore
{
    private readonly object _pendingLock = new();
    // path -> the LATEST pending write for that path (coalescing). Guarded by _pendingLock.
    private readonly Dictionary<string, PendingWrite> _pending = new(StringComparer.Ordinal);
    private readonly ManualResetEventSlim _writeSignal = new(false);
    private volatile bool _writerStop;
    private Thread? _writerThread;   // started lazily on first enqueue, guarded by _pendingLock.

    /// <summary>Hands a completed serialization off to the background writer and returns at once.
    /// The hash has already been recorded on the game thread (see <see cref="SaveCore"/>) so a watcher
    /// event that races the real write is still recognised as our own.</summary>
    private void EnqueueWrite(string pluginGuid, string path, string serialized)
    {
        lock (_pendingLock)
        {
            _pending[path] = new PendingWrite(pluginGuid, serialized);
            EnsureWriterStarted();
        }

        // Never dispose _writeSignal (see Dispose) so this stays safe even after a Dispose+Save, as
        // the config tests drive it. Guarded regardless so a stray race can never bubble out of Save.
        try { _writeSignal.Set(); }
        catch (ObjectDisposedException) { /* writer already torn down */ }
    }

    // Must be called under _pendingLock. Does not restart a stopped writer (post-Dispose the test
    // harness drives writes synchronously via FlushPendingWritesForTest instead).
    private void EnsureWriterStarted()
    {
        if (_writerThread is not null || _writerStop) return;
        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "Stellar-ConfigWriter",
        };
        _writerThread.Start();
    }

    private void WriterLoop()
    {
        while (!_writerStop)
        {
            try { _writeSignal.Wait(); }
            catch (ObjectDisposedException) { return; }
            // Reset BEFORE snapshotting so an enqueue that lands after the snapshot re-signals and is
            // picked up on the next loop — no lost wakeup, no missed write.
            _writeSignal.Reset();
            DrainPending();
        }

        // Stop was requested while we may still hold queued work — write it before exiting.
        DrainPending();
    }

    /// <summary>Snapshots and clears the pending map under the lock, then writes each entry OUTSIDE
    /// the lock. Whole body is guarded so one failed write can NEVER kill the writer thread.</summary>
    private void DrainPending()
    {
        List<KeyValuePair<string, PendingWrite>> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
            batch = new List<KeyValuePair<string, PendingWrite>>(_pending);
            _pending.Clear();
        }

        foreach (var kv in batch)
            WriteOne(kv.Key, kv.Value);
    }

    /// <summary>Performs one disk write and mirrors the CURRENT post-write steps: record the on-disk
    /// stat (so the watcher echo is recognised without a read), then log. The hash fallback in
    /// <c>HandleFileTouch</c> already covers a watcher event that races this write, so it is fine for
    /// the stat to be recorded only now, on the worker thread.</summary>
    private void WriteOne(string path, PendingWrite write)
    {
        try
        {
            File.WriteAllText(path, write.Serialized);
            RecordWrittenStat(write.PluginGuid, path);
            LogSaved(write.PluginGuid, write.Serialized.Length);
        }
        catch (IOException ex)
        {
            SafeWarn($"[Stellar][PluginConfig] save failed: {write.PluginGuid}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            SafeWarn($"[Stellar][PluginConfig] save denied: {write.PluginGuid}: {ex.Message}");
        }
        catch (Exception ex)
        {
            // Last line of defence: the writer thread must survive ANY write failure.
            SafeWarn($"[Stellar][PluginConfig] save threw: {write.PluginGuid}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void SafeWarn(string message)
    {
        try { _log.Warning(message); }
        catch { /* even the warn-log must not propagate out of the writer thread */ }
    }

    /// <summary>Stops the writer, joins it (short timeout), then synchronously flushes anything still
    /// queued so nothing is lost on framework unload / game exit. Called from <see cref="Dispose"/>
    /// BEFORE the watcher teardown.</summary>
    private void ShutdownWriter()
    {
        _writerStop = true;
        try { _writeSignal.Set(); } catch (ObjectDisposedException) { }

        var thread = _writerThread;
        if (thread is not null)
        {
            try { thread.Join(TimeSpan.FromSeconds(2)); }
            catch { /* best effort — the final drain below is the durability guarantee */ }
        }

        // Durability: flush any writes the worker had not yet taken (or that raced the join).
        try { DrainPending(); } catch { /* best effort */ }

        // Deliberately do NOT dispose _writeSignal: a Dispose()+Save() sequence (used by the config
        // tests to detach the live watcher while keeping Save usable) must not throw here.
    }

    /// <summary>Test-only synchronous flush: drains the pending queue on the CALLING thread so a test
    /// can assert the file exists / has content right after <c>Save</c> without making the in-game
    /// path synchronous. Safe to call whether or not the writer thread is running.</summary>
    internal void FlushPendingWritesForTest() => DrainPending();

    private readonly struct PendingWrite
    {
        internal PendingWrite(string pluginGuid, string serialized)
        {
            PluginGuid = pluginGuid;
            Serialized = serialized;
        }

        internal string PluginGuid { get; }
        internal string Serialized { get; }
    }
}
