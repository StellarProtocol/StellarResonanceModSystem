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

    public LiveLoadoutState? LiveState => _probe.ReadLiveState();

    public event Action? LoadoutsChanged;

    public event Action? LiveStateChanged;

    public Task<LoadoutResult> ApplyAsync(int index, CancellationToken ct = default)
        => _probe.CallApplyAsync(index, ct);

    /// <summary>Re-poll the probe, then — LAST — raise the post-parse
    /// <see cref="LiveStateChanged"/> event, so a handler that reads <see cref="GetSlots"/> /
    /// <see cref="LiveState"/> already sees the changed setup. The probe raises its flag ONLY on a
    /// structural difference, so an identical re-read fires nothing (pinned:
    /// <c>LoadoutServiceTests.Tick_raises_LiveStateChanged_only_when_the_probe_reports_a_change</c>).</summary>
    public void Tick()
    {
        RefreshSlots();
        if (_probe.ConsumeLiveStateChanged()) LiveStateChanged?.Invoke();
    }

    /// <summary>Re-poll the probe; rebuild the snapshot and fire <see cref="LoadoutsChanged"/> on change.</summary>
    private void RefreshSlots()
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
            slots.Add(new LoadoutSlot(e.Index, e.Name, e.Index == current, e.ProfessionId, e.TalentStageId, e.TalentNodes, e.Gear, e.Modules));
        }
        _slots = slots;
        LoadoutsChanged?.Invoke();
    }

    /// <summary>Reset the loadout snapshot on logout (account/character-scoped session data). Does
    /// NOT fire <see cref="LoadoutsChanged"/> — a logout is teardown, not a live loadout edit. The
    /// sentinel signature guarantees the next <see cref="Tick"/> after login rebuilds and re-fires
    /// normally.</summary>
    internal void ClearSession()
    {
        _slots = Array.Empty<LoadoutSlot>();
        _currentIndex = null;
        _signature = "\0";
    }

    private static string BuildSignature(IReadOnlyList<LoadoutEntry> entries, int? current)
    {
        var sb = new StringBuilder();
        sb.Append(current?.ToString() ?? "-").Append('|');
        foreach (var e in entries)
        {
            // ProfessionId/TalentStageId are included so a class remap or talent
            // respec (list + selection otherwise unchanged) still re-fires
            // LoadoutsChanged. Gear/Modules counts are folded in because per-class
            // gear/modules resolve a beat AFTER the base fields (they need the item
            // container): without this the base signature is unchanged when they land,
            // so _slots would keep the null-gear snapshot and never surface the gear.
            sb.Append(e.Index).Append(':').Append(e.Name).Append(':')
              .Append(e.ProfessionId).Append(':').Append(e.TalentStageId).Append(':')
              .Append(e.Gear?.Count ?? -1).Append(':').Append(e.Modules?.Count ?? -1).Append(';');
        }
        return sb.ToString();
    }
}
