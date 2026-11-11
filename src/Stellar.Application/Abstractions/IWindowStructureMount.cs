using Stellar.Abstractions.Services;

namespace Stellar.Application.Abstractions;

/// <summary>Optional renderer capability behind <c>WindowSpec.Prebuild</c>'s first stage (WindowService.Prebuild.cs): build
/// a window's element tree at the login / character-select screen WITHOUT its first value apply, and leave it parked.
/// A renderer without it simply skips that stage (the in-world fill then mounts the window off-screen instead).</summary>
internal interface IWindowStructureMount
{
    /// <summary>Build <paramref name="reg"/>'s tree with no value pulled (no text / visibility / list lambda, no
    /// Conditional branch) and return it PARKED — root inactive, never drawn, never hit-tested. Its next
    /// <see cref="IWindowRenderer.ApplyValues"/> is its first apply. Null = canvas not available / build threw.</summary>
    object? MountStructure(WindowRegistration reg);
}
