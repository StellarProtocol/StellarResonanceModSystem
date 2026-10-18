using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="IWindowHost"/> view — the same decoration <c>PerPluginHotkeys</c> and
/// <c>PerPluginLauncher</c> use. Forwards every <see cref="Register(WindowRegistration)"/> to the shared window
/// service tagged with this plugin's guid, so the layout editor can attribute each window to its owning plugin
/// (and filter edit-mode to a single plugin's windows). Every non-registering member passes straight through to
/// the shared host, so plugins see an unchanged <see cref="IWindowHost"/> and nothing else moves.
/// </summary>
internal sealed class PerPluginWindowHost : IWindowHost
{
    private readonly string _pluginGuid;
    private readonly IWindowOwnedRegistrations _sink;
    private readonly IWindowHost _shared;

    public PerPluginWindowHost(string pluginGuid, IWindowOwnedRegistrations sink, IWindowHost shared)
    {
        _pluginGuid = pluginGuid;
        _sink = sink;
        _shared = shared;
    }

    public IWindowControl Register(WindowRegistration registration) => _sink.Register(registration, _pluginGuid);

    public IWindowControl Register(WindowRegistration registration, HotkeyAction toggleAction, IHotkeys hotkeys)
        => _sink.Register(registration, toggleAction, hotkeys, _pluginGuid);

    public IWindowControl? Find(string id) => _shared.Find(id);

    public bool IsLayoutEditing => _shared.IsLayoutEditing;
}
