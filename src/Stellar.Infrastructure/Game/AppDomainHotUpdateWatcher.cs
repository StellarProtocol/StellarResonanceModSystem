using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

internal sealed class AppDomainHotUpdateWatcher
{
    private readonly IPluginLog _log;
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private HashSet<string>? _expected;
    private Action? _callback;
    private bool _fired;

    public AppDomainHotUpdateWatcher(IPluginLog log) => _log = log;

    public void WaitForAll(IReadOnlyCollection<string> expectedAssemblies, Action onAllLoaded)
    {
        _expected = new HashSet<string>(expectedAssemblies, StringComparer.Ordinal);
        _callback = onAllLoaded;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Observe(assembly.GetName().Name);
        }

        AppDomain.CurrentDomain.AssemblyLoad += (_, args) => Observe(args.LoadedAssembly.GetName().Name);
    }

    private void Observe(string? name)
    {
        if (_fired || name is null || _expected is null || !_expected.Contains(name))
        {
            return;
        }
        if (!_seen.Add(name))
        {
            return;
        }
        _log.Info($"[hot-update] {name}  ({_seen.Count}/{_expected.Count})");
        if (_seen.SetEquals(_expected))
        {
            _fired = true;
            _callback?.Invoke();
        }
    }
}
