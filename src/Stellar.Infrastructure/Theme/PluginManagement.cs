using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Theme;

/// <summary>
/// Phase 8 stub. Returns the list of discovered plugins as IsEnabled=true.
/// Phase 9 wires <see cref="SetEnabled"/> to call plugin.Dispose() + re-construct.
/// </summary>
internal sealed class PluginManagement : IPluginManagement
{
    private readonly IReadOnlyList<PluginStatus> _initialList;

    public PluginManagement(IReadOnlyList<PluginStatus> initialList)
    {
        _initialList = initialList;
    }

    public IReadOnlyList<PluginStatus> List() => _initialList;

    public void SetEnabled(string pluginId, bool enabled)
    {
        // Phase 9 implements this.
    }
}
