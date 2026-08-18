using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Registers a plugin's embedded <c>Lang/*.json</c> catalogs and returns its scoped
/// <see cref="ILocalization"/> façade. Called once per plugin at load; bundled into the
/// per-plugin resource factories so <c>PluginHost</c> stays within the ctor-dependency cap.
/// </summary>
internal interface ILocalizationHost
{
    /// <summary>Discover and register <paramref name="asm"/>'s <c>Lang/&lt;code&gt;.json</c> resources
    /// under namespace <paramref name="ns"/>, then return the scoped façade for that namespace.</summary>
    ILocalization RegisterPlugin(string ns, Assembly asm);
}
