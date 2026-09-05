// src/Stellar.Infrastructure/Configuration/FileConfigStore.cs
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Configuration;

/// <summary>
/// JSON-on-disk implementation of <see cref="IConfigStore"/>. Each plugin's
/// config lives at <c>&lt;pluginDir&gt;/&lt;pluginGuid&gt;.config.json</c>.
/// A single <see cref="FileSystemWatcher"/> covers the directory; external
/// edits are told apart from our own writes by <see cref="SelfWriteLedger"/>
/// (stat first, content hash as the fallback). Watcher events fire on a
/// background thread — the public <see cref="DrainExternalEvents"/> is invoked
/// from the game thread (BootstrapPlugin.OnGameUpdate) and is the boundary at
/// which <see cref="ExternalFileChanged"/> is raised.
/// </summary>
internal sealed partial class FileConfigStore : IConfigStore, IDisposable
{
    private const string ConfigSuffix = ".config.json";
    internal static readonly TimeSpan SelfWriteTtl = TimeSpan.FromSeconds(5);
    // Deliberately much shorter than SelfWriteTtl — see SelfWriteLedger for why (an early expiry
    // costs a read, never correctness).
    internal static readonly TimeSpan SelfWriteStatTtl = TimeSpan.FromSeconds(1);

    // Compact, NOT indented. Readability of the on-disk file is deliberately traded for keeping a
    // save off the large-object heap: indenting the owner's nested wardrobe config inflated it from
    // ~20 K to ~55 K chars, i.e. a >85 KB string (LOH threshold is 85 000 bytes) allocated on EVERY
    // save, which is what turned a save click into a gen2 GC pause (owner report 2026-09-05).
    // Reading is unaffected — the parser accepts indented files, so configs written by older builds
    // (and by hand) still load.
    private static readonly JsonSerializerOptions SaveJsonOptions = new() { WriteIndented = false };

    private readonly IPluginLog _log;
    private readonly string _pluginsDirPath;
    private readonly FileSystemWatcher? _watcher;
    private readonly SelfWriteLedger _selfWrites = new(SelfWriteTtl, SelfWriteStatTtl);
    private readonly ConcurrentQueue<string> _externalEventQueue = new();
    private readonly Func<string, string> _readWatchedFile;

    public event Action<string>? ExternalFileChanged;

    /// <param name="log">Framework log sink.</param>
    /// <param name="pluginsDirPath">Directory holding the per-plugin config files.</param>
    /// <param name="readWatchedFile">
    /// Reader used by the watcher path only. Defaults to <see cref="File.ReadAllText(string)"/>;
    /// unit tests inject a counting reader to pin that a recognised self-write echo performs ZERO
    /// reads (regression pin for the 2026-09-05 save-click freeze).
    /// </param>
    public FileConfigStore(IPluginLog log, string pluginsDirPath, Func<string, string>? readWatchedFile = null)
    {
        _log = log;
        _pluginsDirPath = Path.GetFullPath(pluginsDirPath);
        _readWatchedFile = readWatchedFile ?? File.ReadAllText;

        try
        {
            Directory.CreateDirectory(_pluginsDirPath);
            _watcher = new FileSystemWatcher(_pluginsDirPath, "*" + ConfigSuffix)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileRenamed;
            _watcher.Deleted += OnFileDeleted;
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][PluginConfig] watcher init failed for {_pluginsDirPath}: {ex.GetType().Name}: {ex.Message}");
            _watcher = null;
        }

        _log.Info($"[Stellar][PluginConfig] file store ready, watching {_pluginsDirPath}");
    }

    public bool TryLoad(string pluginGuid, out JsonNode? root)
    {
        root = null;
        if (!TryResolvePath(pluginGuid, out var path)) return false;
        if (!File.Exists(path)) return false;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            _log.Warning($"[Stellar][PluginConfig] read failed: {pluginGuid}: {ex.Message}");
            return false;
        }

        try
        {
            root = JsonNode.Parse(text);
            LogLoaded(pluginGuid, text.Length, root);
            return root is not null;
        }
        catch (JsonException ex)
        {
            QuarantineCorruptFile(path, ex);
            root = null;
            return false;
        }
    }

    public void Save(string pluginGuid, JsonNode root)
    {
        if (!TryResolvePath(pluginGuid, out var path)) return;

        var serialized = root.ToJsonString(SaveJsonOptions);
        // Hash BEFORE the write so a watcher event that races File.WriteAllText is still recognised.
        _selfWrites.RecordPending(pluginGuid, ComputeHash(serialized), DateTime.UtcNow);

        try
        {
            File.WriteAllText(path, serialized);
            RecordWrittenStat(pluginGuid, path);
            LogSaved(pluginGuid, serialized.Length);
        }
        catch (IOException ex)
        {
            _log.Warning($"[Stellar][PluginConfig] save failed: {pluginGuid}: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warning($"[Stellar][PluginConfig] save denied: {pluginGuid}: {ex.Message}");
        }
    }

    /// <summary>
    /// Drains the queue of pending external-edit notifications and fires
    /// <see cref="ExternalFileChanged"/> on the caller's thread. Called from
    /// BootstrapPlugin.OnGameUpdate so subscribers run on the game thread.
    /// </summary>
    public void DrainExternalEvents()
    {
        while (_externalEventQueue.TryDequeue(out var pluginGuid))
        {
            try
            {
                ExternalFileChanged?.Invoke(pluginGuid);
            }
            catch (Exception ex)
            {
                _log.Warning($"[Stellar][PluginConfig] subscriber threw for {pluginGuid}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Renamed -= OnFileRenamed;
                _watcher.Deleted -= OnFileDeleted;
                _watcher.Dispose();
            }
        }
        catch { /* dispose is best-effort */ }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
        => HandleFileTouch(e.FullPath, e.Name);

    private void OnFileRenamed(object sender, RenamedEventArgs e)
        // Only the post-rename name (which matches the *.config.json filter) is
        // delivered here; the old name is irrelevant. Treat like a Created event.
        => HandleFileTouch(e.FullPath, e.Name);

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        try { LogDeleted(e.Name); }
        catch { /* logging must never throw out of the watcher thread */ }
    }

    /// <summary>
    /// Common watcher handler — extracts the plugin GUID, suppresses self-write
    /// echoes (stat first, content hash as the fallback), otherwise queues the
    /// GUID for thread-marshaled delivery in <see cref="DrainExternalEvents"/>.
    /// MUST NEVER throw out — wraps the entire body in a catch-all so the
    /// game can't be brought down by an IO error from the watcher thread.
    /// <para>Internal (not private) so unit tests can drive it deterministically
    /// instead of racing the real <see cref="FileSystemWatcher"/>.</para>
    /// </summary>
    internal void HandleFileTouch(string fullPath, string? fileName)
    {
        try
        {
            if (!TryPluginGuidFrom(fileName, out var pluginGuid)) return;
            var now = DateTime.UtcNow;

            // Stat pre-filter: one FileInfo recognises the echo of our own write with no read and no
            // hash (rationale + the 2026-09-05 measurements are on SelfWriteLedger).
            if (TryStat(fullPath, out var length, out var lastWriteUtc)
                && _selfWrites.IsStatEcho(pluginGuid, length, lastWriteUtc, now))
            {
                LogEchoSuppressed(pluginGuid);
                return;
            }

            string text;
            try
            {
                text = _readWatchedFile(fullPath);
            }
            catch (IOException) { return; }       // file still being written / locked
            catch (UnauthorizedAccessException) { return; }

            // Hash fallback, for an event delivered while our own File.WriteAllText was still running
            // (that write's stat is not recorded yet, so the filter above cannot match it). Both
            // checks PEEK, never consume — see SelfWriteLedger for why that is load-bearing.
            if (_selfWrites.IsHashEcho(pluginGuid, ComputeHash(text), now))
            {
                LogEchoSuppressed(pluginGuid);
                return;
            }

            _externalEventQueue.Enqueue(pluginGuid);
            LogExternalEditQueued(pluginGuid);
        }
        catch (Exception ex)
        {
            try { _log.Warning($"[Stellar][PluginConfig] watcher handler threw: {ex.GetType().Name}: {ex.Message}"); }
            catch { /* even the warn-log must not propagate */ }
        }
    }

    private static bool TryPluginGuidFrom(string? fileName, out string pluginGuid)
    {
        pluginGuid = string.Empty;
        if (string.IsNullOrEmpty(fileName)) return false;
        if (!fileName!.EndsWith(ConfigSuffix, StringComparison.Ordinal)) return false;
        pluginGuid = fileName.Substring(0, fileName.Length - ConfigSuffix.Length);
        return pluginGuid.Length > 0;
    }

    /// <summary>Cheap existence + size + mtime probe. False when the file vanished or the stat
    /// itself failed — the caller then falls through to the (correct, just costlier) hash path.</summary>
    private static bool TryStat(string fullPath, out long length, out DateTime lastWriteUtc)
    {
        length = -1;
        lastWriteUtc = default;
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists) return false;
            length = info.Length;
            lastWriteUtc = info.LastWriteTimeUtc;
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Records the stat of the file we just wrote, so the watcher events it provokes are
    /// recognised without a read. Best-effort: on failure the hash path still catches them.</summary>
    private void RecordWrittenStat(string pluginGuid, string path)
    {
        if (!TryStat(path, out var length, out var lastWriteUtc)) return;
        _selfWrites.RecordCompleted(pluginGuid, length, lastWriteUtc, DateTime.UtcNow);
    }

    private bool TryResolvePath(string pluginGuid, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrEmpty(pluginGuid)) return false;

        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(_pluginsDirPath, pluginGuid + ConfigSuffix));
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][PluginConfig] invalid guid '{pluginGuid}': {ex.Message}");
            return false;
        }

        var dirWithSep = _pluginsDirPath.EndsWith(Path.DirectorySeparatorChar)
            ? _pluginsDirPath
            : _pluginsDirPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(dirWithSep, StringComparison.Ordinal))
        {
            _log.Warning($"[Stellar][PluginConfig] path traversal rejected: {pluginGuid}");
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private void QuarantineCorruptFile(string path, Exception parseEx)
    {
        var quarantined = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            File.Move(path, quarantined);
            _log.Warning($"[Stellar][PluginConfig] corrupt file moved: {path} -> {quarantined} ({parseEx.Message})");
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][PluginConfig] corrupt file detected but rename failed: {path} ({ex.GetType().Name}: {ex.Message}; parse: {parseEx.Message})");
        }
    }

    private static string ComputeHash(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(bytes);
        return Convert.ToBase64String(digest);
    }
}
