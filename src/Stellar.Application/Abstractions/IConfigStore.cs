// src/Stellar.Application/Abstractions/IConfigStore.cs
using System;
using System.Text.Json.Nodes;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — Application asks Infrastructure to load / save a
/// plugin's config JSON file and observe external edits to it. Implemented in
/// <c>Stellar.Infrastructure</c> by <c>FileConfigStore</c> (JSON on disk plus
/// <c>FileSystemWatcher</c> with echo suppression).
/// </summary>
internal interface IConfigStore
{
    /// <summary>
    /// Attempts to load the persisted JSON document for
    /// <paramref name="pluginGuid"/>. Returns true with a populated root on
    /// success; false (and a null root) if the file is missing or malformed.
    /// Malformed files are renamed to
    /// <c>&lt;file&gt;.corrupt-&lt;timestamp&gt;</c> for forensic inspection
    /// and treated as missing.
    /// </summary>
    bool TryLoad(string pluginGuid, out JsonNode? root);

    /// <summary>
    /// Persists <paramref name="root"/> to disk for
    /// <paramref name="pluginGuid"/>. Records the write so the
    /// FileSystemWatcher echo for this write is suppressed.
    /// </summary>
    void Save(string pluginGuid, JsonNode root);

    /// <summary>
    /// Fired when an external edit (NOT a self-Save) is detected for any
    /// plugin's config file. The string argument is the plugin GUID. Fired on
    /// the game thread via the IFramework callback queue (Application
    /// marshals; Infrastructure raises off-thread).
    /// </summary>
    event Action<string>? ExternalFileChanged;
}
