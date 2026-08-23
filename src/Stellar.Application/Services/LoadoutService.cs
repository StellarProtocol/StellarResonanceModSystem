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

    // The probe list OBJECT the current _slots snapshot was projected from. Reference identity is the
    // EXACT, allocation-free "has the probe served something new" test, because the probe REPLACES its
    // _loadouts list wholesale on every re-parse / re-resolve and never mutates it in place.
    //
    // ROOT CAUSE it fixes (owner staging run sea/P073ErzDAx, 2026-08-23): _slots used to be gated on
    // BuildSignature, which folds in only the gear/module COUNTS. A "Replace" — the owner's in-town ring
    // swap and module swap alike — keeps every count identical (11 gear, 5 modules), so the signature was
    // byte-identical, RefreshSlots returned early, and GetSlots() kept serving the PRE-Replace gear for
    // the rest of the session. Measured in that boot's log: on the merge tick the probe had already
    // resolved the new ring (`[PerClassLoadout] … 207:2070912`, line 9819) while the plugin's very next
    // capture still read the old one (`[ClassGearDiag] … 207:2071330`, line 9829) — so the captured setup
    // identity compared EQUAL and no new setup was ever minted. A notification dedupe must never gate
    // DATA: reference identity now decides what we SERVE, the signature only what we ANNOUNCE.
    private IReadOnlyList<LoadoutEntry>? _servedEntries;

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

    /// <summary>Re-project the probe's snapshot, then fire <see cref="LoadoutsChanged"/> if the saved
    /// list or the selection changed.
    ///
    /// <para><b>Two decisions, deliberately separate.</b> WHAT WE SERVE (<see cref="GetSlots"/>) is
    /// re-projected whenever the probe hands out a different list object — exact and O(1), so
    /// <see cref="GetSlots"/> is a pure function of what the probe currently holds and can never lag it.
    /// WHAT WE ANNOUNCE (<see cref="LoadoutsChanged"/>: "the saved-loadout list or the current selection
    /// changed") stays gated on <see cref="BuildSignature"/>, so the field-agnostic container-merge
    /// re-resolve — which replaces the list on every unrelated delta — does not spam subscribers.
    /// Collapsing the two is exactly the defect this fixed: a same-count gear "Replace" is invisible to
    /// the signature, so the served snapshot silently froze (see <c>_servedEntries</c>).</para></summary>
    private void RefreshSlots()
    {
        var entries = _probe.ReadLoadouts();
        var current = _probe.ReadCurrentIndex();
        if (ReferenceEquals(entries, _servedEntries) && current == _currentIndex)
        {
            return;   // the probe re-served the very same snapshot — nothing to project
        }

        _servedEntries = entries;
        _currentIndex = current;
        var slots = new List<LoadoutSlot>(entries.Count);
        foreach (var e in entries)
        {
            slots.Add(new LoadoutSlot(e.Index, e.Name, e.Index == current, e.ProfessionId, e.TalentStageId, e.TalentNodes, e.Gear, e.Modules));
        }
        _slots = slots;

        var signature = BuildSignature(entries, current);
        if (signature == _signature)
        {
            return;   // same saved list + selection — a re-resolve, not a loadout change
        }

        _signature = signature;
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
        _servedEntries = null;   // force the next Tick to re-project, whatever the probe still holds
    }

    /// <summary>NOTIFICATION signature only — see <see cref="RefreshSlots"/>. It is deliberately COARSE
    /// (identity of the saved list + selection, gear/module counts to catch "the per-class detail
    /// landed"); it must never be used to decide whether the served snapshot is fresh, because a
    /// same-count item swap does not move it.</summary>
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
