// tests/Stellar.Application.Tests/Config/StubConfigStore.cs
using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests.Config;

internal sealed class StubConfigStore : IConfigStore
{
    public Dictionary<string, JsonNode?> Files { get; } = new();
    public List<(string PluginGuid, JsonNode Root)> SaveCalls { get; } = new();

    /// <summary>The node references handed to <see cref="Save"/>, for IDENTITY assertions only
    /// (the no-clone pin). Deliberately never read for content — the interface contract forbids an
    /// implementation from retaining the caller's live tree.</summary>
    public List<JsonNode> LiveNodesSeen { get; } = new();

    public event Action<string>? ExternalFileChanged;

    public bool TryLoad(string pluginGuid, out JsonNode? root)
    {
        if (Files.TryGetValue(pluginGuid, out var node) && node is not null)
        {
            root = node.DeepClone();
            return true;
        }
        root = null;
        return false;
    }

    public void Save(string pluginGuid, JsonNode root)
    {
        // Snapshot on BOTH sides. IConfigStore.Save receives the caller's LIVE tree (it must
        // serialize synchronously and retain nothing — see the interface contract), so a stub that
        // stashed the node itself would report later mutations as if they had been saved.
        LiveNodesSeen.Add(root);
        SaveCalls.Add((pluginGuid, root.DeepClone()));
        Files[pluginGuid] = root.DeepClone();
    }

    /// <summary>Test helper — simulates an external file edit.</summary>
    public void RaiseExternalChange(string pluginGuid, JsonNode? newRoot)
    {
        Files[pluginGuid] = newRoot;
        ExternalFileChanged?.Invoke(pluginGuid);
    }
}
