using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="IHotkeys"/> view — the same decoration pattern
/// <c>Config</c>, <c>Data</c> and <c>Framework</c> already use in
/// <see cref="PerPluginServices"/>. Forwards every declare to the shared
/// <c>HotkeyService</c> tagged with this plugin's guid, so the Settings →
/// Hotkeys panel can group rows by real plugin identity.
///
/// Previously <c>PerPluginServices.Hotkeys</c> was a plain pass-through, which
/// dropped the caller's identity at the boundary and forced the panel to infer
/// the owner from the action id's prefix — a plugin-chosen string, not identity.
///
/// Plugins need no change: they still see the 1-member <see cref="IHotkeys"/>.
/// This also covers <c>IWindowHost.Register(reg, toggleAction, hotkeys)</c>,
/// because plugins hand it their own <c>IPluginServices.Hotkeys</c> — i.e. this.
/// </summary>
internal sealed class PerPluginHotkeys : IHotkeys
{
    private readonly string _pluginGuid;
    private readonly IHotkeyOwnedDeclarations _sink;

    public PerPluginHotkeys(string pluginGuid, IHotkeyOwnedDeclarations sink)
    {
        _pluginGuid = pluginGuid;
        _sink = sink;
    }

    public IHotkeyAction DeclareAction(HotkeyAction action, Action callback)
        => _sink.DeclareAction(action, callback, _pluginGuid);
}
