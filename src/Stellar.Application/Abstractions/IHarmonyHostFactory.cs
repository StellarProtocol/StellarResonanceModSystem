using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port for minting a per-plugin <see cref="IHarmonyHost"/>. Implemented by Infrastructure
/// (<c>HarmonyHostFactory</c>) because the concrete host owns <see cref="HarmonyLib.Harmony"/> instances,
/// which live in the game-interop layer. <see cref="PluginHost"/> calls this once per loaded plugin so each
/// plugin's Harmony instances are id-namespaced to it and unpatched when it is disposed.
/// <para>The returned host also implements <see cref="System.IDisposable"/>; the caller disposes it on plugin
/// teardown to unpatch every instance the plugin created.</para>
/// </summary>
public interface IHarmonyHostFactory
{
    /// <summary>Creates a Harmony host scoped to <paramref name="pluginId"/>.</summary>
    /// <param name="pluginId">The loaded plugin's id (its lowercased assembly name).</param>
    IHarmonyHost Create(string pluginId);
}
