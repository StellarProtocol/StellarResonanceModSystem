using System;

namespace Stellar.Application.Services;

/// <summary>
/// Tracks two damage-per-second numbers for one entity. Both values use the
/// encounter-average formula (matches BPSR-Meter's <c>total_dps</c> in
/// <c>dataManager.ts</c>): total damage in the current encounter divided by
/// elapsed time, floored at 1 second so a single-hit encounter reads as
/// "damage per second" instead of "damage per millisecond".
/// <list type="bullet">
///   <item><see cref="Live"/> — encounter total / max(1s, last - first). Frozen
///   between hits — only <see cref="RecordDamage"/> updates the value.</item>
///   <item><see cref="Encounter"/> — identical formula; retained for API
///   compatibility with earlier stages that distinguished the two.</item>
/// </list>
/// An encounter ends after 30 seconds of no damage; the next hit starts a fresh
/// encounter with total reset to that hit's amount.
/// </summary>
internal sealed class DpsAccumulator
{
    private const long EncounterIdleMs = 30_000;

    private long _encounterTotal;
    private long _encounterStartMs;
    private long _encounterLastMs;

    public long Live      { get; private set; }
    public long Encounter { get; private set; }

    public void RecordDamage(long timestampMs, long amount)
    {
        if (amount <= 0) return;

        if (_encounterStartMs == 0 || (timestampMs - _encounterLastMs) > EncounterIdleMs)
        {
            // New encounter (first hit ever OR > 30s since last hit).
            _encounterStartMs = timestampMs;
            _encounterTotal   = 0;
        }
        _encounterLastMs = timestampMs;
        _encounterTotal += amount;

        Recompute();
    }

    private void Recompute()
    {
        long span  = Math.Max(1_000L, _encounterLastMs - _encounterStartMs);
        long value = _encounterTotal * 1000L / span;
        Live      = value;
        Encounter = value;
    }
}
