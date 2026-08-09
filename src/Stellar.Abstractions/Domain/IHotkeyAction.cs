using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Handle returned by <see cref="Services.IHotkeys.DeclareAction"/>. Dispose to unregister the action.</summary>
public interface IHotkeyAction : IDisposable
{
    /// <summary>The stable id this action was registered with.</summary>
    string      Id              { get; }
    /// <summary>The currently active user binding, or null when no binding is set.</summary>
    KeyBinding? CurrentBinding  { get; }
    /// <summary>
    /// Guid of the plugin that declared this action, or null for framework-declared
    /// actions (those go straight to the shared hotkey service, not through a
    /// per-plugin <c>IHotkeys</c>). Lets the Settings → Hotkeys panel group by real
    /// plugin identity instead of guessing it from the id prefix.
    /// </summary>
    string?     PluginId        { get; }
    /// <summary>The human-readable label from <see cref="HotkeyAction.Description"/>. Never null (empty when undeclared).</summary>
    string      Description     { get; }
}
