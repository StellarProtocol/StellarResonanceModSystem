using System;

namespace Stellar.Abstractions.Plugins;

/// <summary>
/// Implemented by every Stellar plugin. The plugin host instantiates the type via its
/// single constructor that takes <see cref="Services.IPluginServices"/>.
/// </summary>
public interface IStellarPlugin : IDisposable
{
    /// <summary>Human-readable plugin name. Surfaced in logs and (later) the plugin manager UI.</summary>
    string Name { get; }
}
