using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="ILauncher"/> view — same decoration as <see cref="PerPluginHotkeys"/>. Forwards every
/// <see cref="Register"/> to the shared launcher registry tagged with this plugin's guid, so Settings can show the
/// plugin under its own (localized) launcher tile title. Plugins need no change: they still see an
/// <see cref="ILauncher"/>, and <see cref="Entries"/> is the shared list.
/// </summary>
internal sealed class PerPluginLauncher : ILauncher
{
    private readonly string _pluginGuid;
    private readonly ILauncherOwnedRegistrations _sink;
    private readonly ILauncher _shared;

    public PerPluginLauncher(string pluginGuid, ILauncherOwnedRegistrations sink, ILauncher shared)
    {
        _pluginGuid = pluginGuid;
        _sink = sink;
        _shared = shared;
    }

    public IDisposable Register(LauncherEntry entry) => _sink.Register(entry, _pluginGuid);

    public IReadOnlyList<LauncherEntry> Entries => _shared.Entries;
}
