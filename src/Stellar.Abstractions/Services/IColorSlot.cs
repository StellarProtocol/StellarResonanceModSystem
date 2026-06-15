// src/Stellar.Abstractions/Services/IColorSlot.cs
using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>Handle returned by <see cref="IColorRegistry"/>'s <c>Register</c> methods. Read
/// <see cref="Value"/> when drawing — it resolves to the colour for the
/// active theme, honouring user overrides. Cache the handle, not the value.
/// <para>Disposing a slot unregisters it from the owning <see cref="IColorRegistry"/>.</para></summary>
public interface IColorSlot : IDisposable
{
    /// <summary>Resolves the color for the currently active theme preset, honouring user overrides.</summary>
    ColorRgba Value { get; }
}
