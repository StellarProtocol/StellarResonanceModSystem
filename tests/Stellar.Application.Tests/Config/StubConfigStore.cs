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
        SaveCalls.Add((pluginGuid, root));
        Files[pluginGuid] = root.DeepClone();
    }

    /// <summary>Test helper — simulates an external file edit.</summary>
    public void RaiseExternalChange(string pluginGuid, JsonNode? newRoot)
    {
        Files[pluginGuid] = newRoot;
        ExternalFileChanged?.Invoke(pluginGuid);
    }
}
