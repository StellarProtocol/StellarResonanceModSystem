namespace Stellar.PluginContracts;

/// <summary>
/// Inter-plugin contract: a plugin that can display a frozen, session-time view of an entity (the Entity
/// Inspector implements it). A producer (e.g. CombatMeter's history) acquires it via
/// <c>IPluginServices.Exchange.Consume&lt;IFrozenEntityViewer&gt;()</c> and calls <see cref="ShowFrozen"/>;
/// when no provider is registered, <c>Consume</c> returns <c>null</c> and the producer falls back to its own view.
/// </summary>
public interface IFrozenEntityViewer
{
    /// <summary>
    /// Show <paramref name="entity"/> as a frozen, read-only session-time view. Returns <c>false</c> if the
    /// viewer could not display it (the caller may then fall back), <c>true</c> if it took over.
    /// </summary>
    /// <param name="entity">The frozen snapshot to display.</param>
    /// <returns><c>true</c> if shown; <c>false</c> if the viewer declined.</returns>
    bool ShowFrozen(FrozenEntity entity);
}
