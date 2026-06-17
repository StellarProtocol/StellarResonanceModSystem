namespace Stellar.Abstractions.Services;

/// <summary>
/// The sanctioned inter-plugin communication channel. One plugin <see cref="Provide{T}"/>s an implementation of
/// a contract interface; another <see cref="Consume{T}"/>s it — without the two plugins referencing each other
/// (the framework is their only shared reference). The framework brokers purely by <see cref="System.Type"/>
/// and never references any contract type, so this surface stays plugin-agnostic — it is the ONE generic
/// extension point; specific contracts live in a shared contracts assembly (e.g. <c>Stellar.PluginContracts</c>),
/// never here. Late-bind: <c>Consume</c> at use-time (not construction) so plugin load order does not matter.
/// </summary>
public interface IPluginExchange
{
    /// <summary>
    /// Register <paramref name="implementation"/> as the provider of contract <typeparamref name="T"/>. If two
    /// plugins provide the same contract, the last to register wins.
    /// </summary>
    /// <typeparam name="T">The shared contract interface (defined in a contracts assembly, not in this framework).</typeparam>
    /// <param name="implementation">The implementation other plugins will consume.</param>
    void Provide<T>(T implementation) where T : class;

    /// <summary>
    /// Get the registered provider of contract <typeparamref name="T"/>, or <c>null</c> when no plugin provides
    /// it (e.g. the providing plugin is not installed) — callers fall back accordingly.
    /// </summary>
    /// <typeparam name="T">The shared contract interface.</typeparam>
    /// <returns>The provider implementation, or <c>null</c>.</returns>
    T? Consume<T>() where T : class;
}
