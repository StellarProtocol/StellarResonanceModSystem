using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Implementation of <see cref="IResonanceState"/>. Polled at 1Hz off the probe's
/// latched read (Host wires the Lua-bridge loadout probe — the C# CharSerialize
/// mirror served the pre-swap pair after an in-session imagine swap), publishing
/// an immutable equipped-Imagine snapshot via a volatile reference so plugins on
/// the main thread read lock-free.
/// </summary>
internal sealed class ResonanceService : IResonanceState
{
    private static readonly IReadOnlyList<int> Empty = Array.Empty<int>();

    private readonly IResonanceProbe _probe;

    private IReadOnlyList<int> _installed = Empty;

    public ResonanceService(IResonanceProbe probe)
    {
        _probe = probe;
    }

    public IReadOnlyList<int> Installed => Volatile.Read(ref _installed);

    /// <summary>Called at 1Hz from BootstrapPlugin. Reads the equipped ids from
    /// the probe and publishes the new snapshot when it changed.</summary>
    internal void Refresh()
    {
        if (!_probe.TryReadInstalled(out var installed)) return;
        if (SameAs(installed, Volatile.Read(ref _installed))) return;
        Volatile.Write(ref _installed, installed);
    }

    /// <summary>Empty the installed-Imagine snapshot on logout (account/character-scoped session
    /// data). Called by the Host OnLogout dispatcher.</summary>
    internal void ClearSession() => Volatile.Write(ref _installed, Empty);

    private static bool SameAs(IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }
}
