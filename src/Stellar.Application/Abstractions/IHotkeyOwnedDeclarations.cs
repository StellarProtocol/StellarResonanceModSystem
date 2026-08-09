using System;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Framework-internal declaration sink that carries the OWNING plugin's guid
/// alongside the action. Plugins keep the 1-member public <c>IHotkeys</c>
/// (declare-only, no owner argument to get wrong); the host wraps it per plugin
/// in <c>PerPluginHotkeys</c>, which forwards through here so the registered
/// action knows who declared it.
///
/// Without this the owner is unrecoverable at declare time and the Hotkeys panel
/// has to reverse-engineer it from the id prefix — which is a plugin-chosen
/// string, not an identity.
/// </summary>
internal interface IHotkeyOwnedDeclarations
{
    /// <summary>Declare an action on behalf of <paramref name="pluginId"/> (null = framework-owned).</summary>
    IHotkeyAction DeclareAction(HotkeyAction action, Action callback, string? pluginId);
}
