using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// User-selected global chrome control styles — the button + scrollbar look applied
/// to every GlassMenu panel. Persisted with the theme; changing either fires the
/// theme's <see cref="INamedTheme.ActiveChanged"/> so the renderer rebuilds its skin.
/// A plugin may pin its own per-window button and scrollbar style via
/// <see cref="ButtonElement"/>, which the user cannot override here.
/// </summary>
public interface IChromeStyle
{
    /// <summary>Currently active button visual style applied to all GlassMenu panels.</summary>
    MenuButtonStyle ButtonStyle { get; }

    /// <summary>Currently active scrollbar visual style applied to all GlassMenu panels.</summary>
    MenuScrollbarStyle ScrollbarStyle { get; }

    /// <summary>Body opacity (0..1) of the native uGUI window chrome for the ACTIVE theme. Persisted
    /// per-preset so e.g. Default can stay translucent/frosted while Dark/Light read as near-opaque
    /// over bright in-world scenes. A custom theme inherits its base preset's opacity.</summary>
    float WindowOpacity { get; }

    /// <summary>Global font scale (0.8–1.4). Same value as <see cref="INamedTheme.FontScale"/> (the concrete
    /// theme implements both) — exposed here so the uGUI window renderer can scale its text without taking a
    /// second INamedTheme dependency.</summary>
    float FontScale { get; }

    /// <summary>Changes the global button style and persists it with the active theme preset.</summary>
    void SetButtonStyle(MenuButtonStyle style);

    /// <summary>Changes the global scrollbar style and persists it with the active theme preset.</summary>
    void SetScrollbarStyle(MenuScrollbarStyle style);

    /// <summary>Set <see cref="WindowOpacity"/> for the active theme's preset (clamped to [0.3, 1]).
    /// Persists + fires <see cref="INamedTheme.ActiveChanged"/> so the window chrome rebakes.</summary>
    void SetWindowOpacity(float opacity);
}
