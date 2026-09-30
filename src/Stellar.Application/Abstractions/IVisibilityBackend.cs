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
}
