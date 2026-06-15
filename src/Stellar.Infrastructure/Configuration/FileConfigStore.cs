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
/// edits are detected by hashing the file contents and comparing against a
/// short-TTL cache of self-write hashes (echo suppression). Watcher events
/// fire on a background thread — the public <see cref="DrainExternalEvents"/>
/// is invoked from the game thread (BootstrapPlugin.OnGameUpdate) and is the
/// boundary at which <see cref="ExternalFileChanged"/> is raised.
/// </summary>
internal sealed partial class FileConfigStore : IConfigStore, IDisposable
{
    private const string ConfigSuffix = ".config.json";
    private static readonly TimeSpan SelfWriteTtl = TimeSpan.FromSeconds(5);

    private readonly IPluginLog _log;
    private readonly string _pluginsDirPath;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, SelfWriteRecord> _selfWriteHashes = new();
    private readonly ConcurrentQueue<string> _externalEventQueue = new();

    public event Action<string>? ExternalFileChanged;

    public FileConfigStore(IPluginLog log, string pluginsDirPath)
    {
        _log = log;
        _pluginsDirPath = Path.GetFullPath(pluginsDirPath);

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

        var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        var hash = ComputeHash(serialized);

        PruneExpiredSelfWrites();
        _selfWriteHashes[BuildSelfWriteKey(pluginGuid, hash)] = new SelfWriteRecord(DateTime.UtcNow);

        try
        {
            File.WriteAllText(path, serialized);
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
    /// Common watcher handler — extracts the plugin GUID, reads + hashes the
    /// file, suppresses self-write echoes, otherwise queues the GUID for
    /// thread-marshaled delivery in <see cref="DrainExternalEvents"/>.
    /// MUST NEVER throw out — wraps the entire body in a catch-all so the
    /// game can't be brought down by an IO error from the watcher thread.
    /// </summary>
    private void HandleFileTouch(string fullPath, string? fileName)
    {
        try
        {
            if (string.IsNullOrEmpty(fileName)) return;
            if (!fileName!.EndsWith(ConfigSuffix, StringComparison.Ordinal)) return;
            var pluginGuid = fileName.Substring(0, fileName.Length - ConfigSuffix.Length);
            if (string.IsNullOrEmpty(pluginGuid)) return;

            string text;
            try
            {
                text = File.ReadAllText(fullPath);
            }
            catch (IOException) { return; }       // file still being written / locked
            catch (UnauthorizedAccessException) { return; }

            var hash = ComputeHash(text);
            var key = BuildSelfWriteKey(pluginGuid, hash);
            // PEEK, don't consume. A single File.WriteAllText raises MULTIPLE
            // watcher events (NotifyFilter spans LastWrite|Size, and Wine/Windows
            // deliver 2-4 Changed notifications per write), all carrying the same
            // content hash. Removing the record on the first event let every
            // subsequent event for the SAME self-write fall through and be
            // misclassified as an external edit — each triggering a full config
            // reload + JSON reparse on the game thread. Leaving the record in
            // place suppresses all duplicate events from one write; the 5s
            // SelfWriteTtl (pruned in Save) bounds how long a stale hash lingers,
            // and a genuine external edit carries a different hash so it still
            // falls through correctly.
            if (_selfWriteHashes.ContainsKey(key))
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

    private static string BuildSelfWriteKey(string pluginGuid, string hash)
        => pluginGuid + "|" + hash;

    private void PruneExpiredSelfWrites()
    {
        var cutoff = DateTime.UtcNow - SelfWriteTtl;
        foreach (var kv in _selfWriteHashes)
        {
            if (kv.Value.Timestamp < cutoff)
            {
                _selfWriteHashes.TryRemove(kv.Key, out _);
            }
        }
    }

    private readonly struct SelfWriteRecord
    {
        public DateTime Timestamp { get; }
        public SelfWriteRecord(DateTime timestamp) => Timestamp = timestamp;
    }
}
