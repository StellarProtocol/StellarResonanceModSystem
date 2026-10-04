using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Bridge to ReShade (via the Stellar add-on) — effects on/off, technique toggles, preset switching and
/// shader/texture search paths.
/// <para>Main thread only. Every setter is ASYNCHRONOUS: it queues a request that ReShade applies at its next frame,
/// and <see cref="Techniques"/> / <see cref="CurrentPreset"/> show the result after the following
/// <see cref="Changed"/>. Without ReShade and the add-on, <see cref="State"/> is <see cref="ReShadeState.NotInstalled"/>
/// and every member is a no-op.</para></summary>
public interface IReShade
{
    /// <summary>Whether ReShade is installed, (re)loading its effects, or ready. Only <see cref="ReShadeState.Ready"/>
    /// means <see cref="Techniques"/> is current and photos carry ReShade's effects.</summary>
    ReShadeState State { get; }
    /// <summary>ReShade's effects on/off (saved). Setting it is asynchronous (applied at ReShade's next frame).</summary>
    bool Enabled { get; set; }
    /// <summary>Techniques of the loaded effects (empty while unavailable or loading). UsesDepth and SizeLocked are only
    /// known for effects under the folders given to <see cref="SetSearchPaths"/>; others are reported as true.</summary>
    IReadOnlyList<ReShadeTechnique> Techniques { get; }
    /// <summary>Turns one technique on/off (saved to the current preset). A technique is identified by its effect file
    /// (<see cref="ReShadeTechnique.EffectFile"/>, e.g. "Clarity.fx") and its name, because names repeat across
    /// effects. Asynchronous: <see cref="Techniques"/> shows it after the next <see cref="Changed"/>.</summary>
    void SetTechnique(string effectFile, string name, bool enabled);
    /// <summary>Full path of the current preset file, or null.</summary>
    string? CurrentPreset { get; }
    /// <summary>Switches ReShade to the preset file at <paramref name="path"/> (created by ReShade if missing).
    /// Held until ReShade lists techniques (it ignores a preset switch before that); a later call replaces a held
    /// one. Asynchronous: <see cref="CurrentPreset"/> shows it after the next <see cref="Changed"/>.</summary>
    void SetPreset(string path);
    /// <summary>REPLACES ReShade's effect and texture search paths with these absolute folders ("**" recursion is
    /// added). ReShade persists them to its ReShade.ini and does a FULL effect reload (every effect recompiles;
    /// <see cref="State"/> goes <see cref="ReShadeState.Loading"/> meanwhile). Calling it again with the same folders
    /// sends nothing.</summary>
    void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders);
    /// <summary>Raised on the main thread when <see cref="State"/>, <see cref="Enabled"/>, the technique list or the
    /// preset changes. It can fire while a <c>IScreenCapture.CaptureAsync</c> is in progress (the capture re-reads
    /// ReShade just before choosing a photo's techniques).</summary>
    event Action? Changed;
}
