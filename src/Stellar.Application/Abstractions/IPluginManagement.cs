using System.Collections.Generic;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Phase 8 declares this interface so Phase 9's Settings UI has a stable contract.
/// Phase 8 implementation is read-only (returns the list of loaded plugins with
/// IsEnabled=true). Phase 9 wires SetEnabled to call Dispose / re-construct.
/// </summary>
internal interface IPluginManagement
{
    IReadOnlyList<PluginStatus> List();

    void SetEnabled(string pluginId, bool enabled);
}

internal sealed record PluginStatus(
    string Id,
    string Name,
    string Version,
    bool IsEnabled,
    bool IsErrored);
