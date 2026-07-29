using System;

namespace Stellar.Abstractions.Domain;

/// <summary>
/// The single source of truth for whether a window/HUD should draw. The framework enacts
/// <c>hide = !ShouldRender()</c> each apply (~10 Hz) — a pull, not a stored flag — so the plugin owns the
/// decision and the framework only flips <c>SetActive</c>. Implemented by <see cref="WindowSpec"/> and
/// <see cref="Services.HudSpec"/>, each carrying a compiler-<c>required</c> <see cref="ShouldRender"/> so
/// omitting it fails the build (the interface just declares the getter; <c>required</c> lives on each record).
/// </summary>
public interface IRenderGated
{
    /// <summary>Returns true to draw, false to hide. Reads whatever it wants — <see cref="GamePhase"/>,
    /// <see cref="GameUIState"/>, the plugin's own state — via the plugin's captured services.</summary>
    Func<bool> ShouldRender { get; }
}
