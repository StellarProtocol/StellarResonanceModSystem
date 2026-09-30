using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>Applies a look through the game's own renderer effects. One active look framework-wide.</summary>
public interface IRenderLook
{
    /// <summary>Activates <paramref name="settings"/>; replaces any previously active look.</summary>
    ILookHandle Apply(LookSettings settings);
    /// <summary>Groups this client can render.</summary>
    LookCapabilities Capabilities { get; }
}

/// <summary>An active look. Dispose to restore the game's own look.</summary>
public interface ILookHandle : IDisposable
{
    /// <summary>Replaces the settings of this look (no-op once inactive).</summary>
    void Update(LookSettings settings);
    /// <summary>False once disposed or replaced by another Apply.</summary>
    bool IsActive { get; }
}
