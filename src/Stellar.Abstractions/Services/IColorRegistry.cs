// src/Stellar.Abstractions/Services/IColorRegistry.cs
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Plugins register the colours they OWN. Key is namespaced
/// "Owner.Concept.Property" (e.g. "PlayerHUD.StaminaBar.Fill"), unique.
/// A <c>Register</c> overload supplies the owner's colour for each built-in
/// preset. Register only colours a plugin owns; to match a theme colour, read
/// <c>Theme.Colors.X</c> directly instead.</summary>
public interface IColorRegistry
{
    /// <summary>Registers a color slot with per-preset defaults.</summary>
    IColorSlot Register(string key, string label, IReadOnlyDictionary<ThemePreset, ColorRgba> defaults);

    /// <summary>
    /// Registers a color slot whose default is the same <paramref name="defaultAll"/> for every
    /// theme preset. Convenience over the per-preset dictionary overload.
    /// </summary>
    IColorSlot Register(string key, string label, ColorRgba defaultAll);

    /// <summary>Unregisters the color slot identified by <paramref name="key"/>.</summary>
    void Unregister(string key);
}
