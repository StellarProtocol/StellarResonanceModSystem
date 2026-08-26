namespace Stellar.Abstractions.Services;

/// <summary>
/// Run-timer identity anchor for the current dungeon run — the split-off companion of
/// <see cref="IDungeonState"/>, which sits exactly at the 8-member interface cap (see its NOTE).
///
/// <para>The run-timer VALUE is rank-latched from several wire sources: the approximate
/// entry-sync clock arrives first and an exact play-start source may UPGRADE it MID-RUN
/// (measured on the 2026-08-25 raid: 1787662588000 → 1787662620000 as the entry countdown
/// resolved). The value alone therefore cannot be compared across time to detect a run
/// boundary — an upgrade is indistinguishable from a restart. <see cref="Epoch"/> is the
/// disambiguator: it increments ONLY when the empty latch slot accepts its first value for a
/// run (the slot is emptied by a new run id or logout), never on a rank upgrade — so "epoch
/// changed" means "the framework re-keyed the run", which is real boundary evidence.</para>
///
/// <para>Populated on the network receive thread; both reads are lock-free and safe from the
/// main thread, same publication contract as <see cref="IDungeonState"/>.</para>
/// </summary>
public interface IRunTimer
{
    /// <summary>Server epoch ms when the current run's timer started — the same rank-latched
    /// value as <see cref="IDungeonState.RunTimerStartMs"/>, surfaced here so identity consumers
    /// need only this interface. 0 when not in a dungeon or not yet seen for the current run.</summary>
    long StartMs { get; }

    /// <summary>Monotonic latch counter: +1 each time the empty run-timer slot accepts its FIRST
    /// value for a run. Never incremented by a mid-run rank upgrade and never rewound within a
    /// session, so equality across two reads means "same run keying" and inequality means "the
    /// run was re-keyed in between".</summary>
    int Epoch { get; }
}
