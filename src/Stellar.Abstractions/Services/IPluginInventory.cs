using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-side surface for the Settings → Plugins panel. Lists every discovered
/// plugin with its current enabled / errored state, fires on state changes,
/// and lets the user request a recovery retry after an exception.
/// </summary>
public interface IPluginInventory
{
    /// <summary>Returns a snapshot of all discovered plugins and their current state.</summary>
    IReadOnlyList<PluginInfo> List();

    /// <summary>Fired whenever any tracked plugin's enabled/errored state changes.</summary>
    event Action<PluginInfo> StatusChanged;

    /// <summary>Asks the registry to clear <c>IsErrored</c> and re-attempt construction next frame.</summary>
    void RequestRetry(string pluginId);
}
