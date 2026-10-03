using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Game-side hide switches. Applies the whole requested set; returns what it could actually hide.</summary>
internal interface IVisibilityBackend
{
    VisibilityLayers Apply(VisibilityLayers requested);

    /// <summary>
    /// Re-issues the game calls for every layer in <paramref name="requested"/> even if unchanged — called when
    /// the game's own photo mode / cutscene ends or a hide target was rebuilt, since those can undo a held hide.
    /// Returns what is now hidden.
    /// </summary>
    VisibilityLayers Reassert(VisibilityLayers requested);

    /// <summary>
    /// Probes whether each layer's game-side target currently resolves, WITHOUT invoking it and without touching
    /// game state. A layer whose reflection target hasn't loaded yet (hot-update assemblies load after this
    /// backend is constructed) is reported available (optimistic); once the type has loaded, a genuinely missing
    /// member is reported unavailable (refined).
    /// </summary>
    VisibilityLayers Available { get; }
}
