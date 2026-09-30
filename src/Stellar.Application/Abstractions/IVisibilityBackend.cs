using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Game-side hide switches. Applies the whole requested set; returns what it could actually hide.</summary>
internal interface IVisibilityBackend
{
    VisibilityLayers Apply(VisibilityLayers requested);
}
