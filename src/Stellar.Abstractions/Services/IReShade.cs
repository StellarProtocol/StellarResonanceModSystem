using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Bridge to ReShade (via the Stellar add-on) — effects on/off, technique toggles, preset switching and
/// shader/texture search paths.</summary>
public interface IReShade
{
    /// <summary>True when ReShade and the Stellar bridge add-on are loaded and ReShade has finished loading effects.</summary>
    bool IsAvailable { get; }
    /// <summary>ReShade's effects on/off (saved).</summary>
    bool Enabled { get; set; }
    /// <summary>Techniques of the loaded effects (empty while unavailable or loading). UsesDepth is only known for
    /// effects under the folders given to <see cref="SetSearchPaths"/>; others are reported as using depth.</summary>
    IReadOnlyList<ReShadeTechnique> Techniques { get; }
    /// <summary>Turns one technique on/off (saved to the current preset). A technique is identified by its effect file
    /// (<see cref="ReShadeTechnique.EffectFile"/>, e.g. "Clarity.fx") and its name, because names repeat across
    /// effects.</summary>
    void SetTechnique(string effectFile, string name, bool enabled);
    /// <summary>Full path of the current preset file, or null.</summary>
    string? CurrentPreset { get; }
    /// <summary>Switches ReShade to the preset file at <paramref name="path"/> (created by ReShade if missing).</summary>
    void SetPreset(string path);
    /// <summary>Where ReShade looks for shaders and textures (absolute folders; "**" recursion is added). Reloads effects.</summary>
    void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders);
    /// <summary>Raised on the main thread when availability, the technique list or the preset changes.</summary>
    event Action? Changed;
}
