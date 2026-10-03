using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Null-object <see cref="IReShade"/> — always unavailable, every mutator a no-op. Host wires this in
/// until a later task replaces it with the real Infrastructure bridge to ReShade.
/// </summary>
internal sealed class UnavailableReShade : IReShade
{
    /// <summary>Shared instance — this service holds no per-call state.</summary>
    public static readonly UnavailableReShade Instance = new();

    /// <inheritdoc/>
    public bool IsAvailable => false;
    /// <inheritdoc/>
    public bool Enabled { get => false; set { } }
    /// <inheritdoc/>
    public IReadOnlyList<ReShadeTechnique> Techniques => Array.Empty<ReShadeTechnique>();
    /// <inheritdoc/>
    public void SetTechnique(string effectFile, string name, bool enabled) { }
    /// <inheritdoc/>
    public string? CurrentPreset => null;
    /// <inheritdoc/>
    public void SetPreset(string path) { }
    /// <inheritdoc/>
    public void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders) { }
    /// <inheritdoc/>
    public event Action? Changed { add { } remove { } }
}
