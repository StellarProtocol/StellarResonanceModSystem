using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Semantic status colours of the active theme: the green / red / amber the house style uses for "on",
/// destructive and cautionary states. They follow the theme (the Light preset darkens them so they stay
/// readable on its light panels) and a custom theme can override each one.
/// </summary>
/// <remarks>
/// Added in framework 2.30.0. On the dark presets the values equal the colours plugins hard-coded before
/// (<c>#5FD38D</c>, <c>#FF4D4F</c>, <c>#FFCC73</c>), so switching a plugin from its own constants to these is
/// visually a no-op there. A plugin that reads them must require framework ≥ 2.30.0.
/// Kept as its own facet (not on <see cref="IThemeMenuColors"/>) to stay inside the STELLAR0005 member cap.
/// </remarks>
public interface IThemeStatusColors
{
    /// <summary>"On" / success / confirmation colour (status text, on-chips, the "✓ done" feedback line).</summary>
    ColorRgba Success { get; }

    /// <summary>Destructive / error colour (the Danger pill fill, the remove-hover wash, failure text).</summary>
    ColorRgba Danger { get; }

    /// <summary>Cautionary colour (the tinted Caution pill, "limit reached" notes, warning chips).</summary>
    ColorRgba Caution { get; }
}
