using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>Wraps <see cref="ILoadoutProbe"/> to expose <see cref="ILoadout"/>. Owns
/// change-detection: <see cref="Tick"/> (driven by the Host service tick) re-reads the
/// probe and raises <see cref="LoadoutsChanged"/> only when the list or selection changes.</summary>
internal sealed class LoadoutService : ILoadout
{
    private readonly ILoadoutProbe _probe;
    private IReadOnlyList<LoadoutSlot> _slots = Array.Empty<LoadoutSlot>();
    private int? _currentIndex;
    private string _signature = "\0";   // sentinel that no real signature equals

    public LoadoutService(ILoadoutProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved;

    public IReadOnlyList<LoadoutSlot> GetSlots() => _slots;

    public int? CurrentIndex => _currentIndex;

    public event Action? LoadoutsChanged;

    public Task<LoadoutResult> ApplyAsync(int index, CancellationToken ct = default)
        => _probe.CallApplyAsync(index, ct);

    /// <summary>Re-poll the probe; rebuild the snapshot and fire the event on change.</summary>
    public void Tick()
    {
        var entries = _probe.ReadLoadouts();
        var current = _probe.ReadCurrentIndex();
        var signature = BuildSignature(entries, current);
        if (signature == _signature)
        {
            return;
        }

        _signature = signature;
        _currentIndex = current;
        var slots = new List<LoadoutSlot>(entries.Count);
        foreach (var e in entries)
        {
            slots.Add(new LoadoutSlot(e.Index, e.Name, e.Index == current));
        }
        _slots = slots;
        LoadoutsChanged?.Invoke();
    }

    private static string BuildSignature(IReadOnlyList<LoadoutEntry> entries, int? current)
    {
        var sb = new StringBuilder();
        sb.Append(current?.ToString() ?? "-").Append('|');
        foreach (var e in entries)
        {
            sb.Append(e.Index).Append(':').Append(e.Name).Append(';');
        }
        return sb.ToString();
    }
}
