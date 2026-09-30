using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>Reference-counted hiding of scene layers. Main thread only.</summary>
public interface ISceneVisibility
{
    /// <summary>Hides <paramref name="layers"/> until the returned token is disposed.</summary>
    IDisposable Hide(VisibilityLayers layers);
    /// <summary>Layers actually hidden right now (unsupported layers are never reported).</summary>
    VisibilityLayers Hidden { get; }
    /// <summary>Raised when <see cref="Hidden"/> changes.</summary>
    event Action<VisibilityLayers>? Changed;
}
