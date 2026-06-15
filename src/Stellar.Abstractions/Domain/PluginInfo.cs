namespace Stellar.Abstractions.Domain;

/// <summary>
/// Snapshot of a plugin slot known to <c>IPluginInventory</c>. The Settings →
/// Plugins panel renders one row per <see cref="PluginInfo"/>.
/// </summary>
public sealed record PluginInfo(
    string Id,
    string DisplayName,
    string Version,
    bool   IsEnabled,
    bool   IsErrored)
{
    /// <summary>Last error captured by the registry while constructing/disposing the plugin.</summary>
    public string? LastErrorMessage { get; init; }
}
