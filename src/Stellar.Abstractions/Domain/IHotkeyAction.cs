using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Handle returned by <see cref="Services.IHotkeys.DeclareAction"/>. Dispose to unregister the action.</summary>
public interface IHotkeyAction : IDisposable
{
    /// <summary>The stable id this action was registered with.</summary>
    string      Id              { get; }
    /// <summary>The currently active user binding, or null when no binding is set.</summary>
    KeyBinding? CurrentBinding  { get; }
}
