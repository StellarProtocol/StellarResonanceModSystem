using System;
using System.Collections.Generic;
using System.Linq;
namespace Stellar.Infrastructure.Game;

/// <summary>The combat-freeze sampler's totals (diagnostics only): the per-sample <c>sample</c> line, and on unfreeze one
/// <c>entity-summary</c> line per sampled monster plus the <c>summary:</c> line with the hypothesis tags
/// (<see cref="FreezeDiagVerdict"/>).</summary>
internal sealed partial class GameFreezeBackend
{
    private const int SummaryEntityLines = 20;
    // This sample's counts: drawn animating, controller animating, off hold, untargeted, not held, untracked.
    private readonly int[] _diagS = new int[6];

    /// <summary>Adds one entity's anomaly bits (bit i = <see cref="_diagS"/>[i]) to this sample's counts.</summary>
    private void DiagSampleCounts(int bits)
    {
        for (var i = 0; i < _diagS.Length; i++)
            if ((bits & (1 << i)) != 0) _diagS[i]++;
    }

    private string DiagSampleLine(long now, List<DiagCand> cands, int logged)
    {
        var line = $"{DiagTag}sample s={_diagClock!.Samples} t={_diagClock.Elapsed(now)} cand={cands.Count} logged={logged} " +
                   $"drawnAnim={_diagS[0]} ctlAnim={_diagS[1]} offHold={_diagS[2]} untargeted={_diagS[3]} unheld={_diagS[4]} " +
                   $"untracked={_diagS[5]} | g: calls={_speedGate.Calls} seen={_speedGate.Seen} offThread={_diagCounters!.OffThreadHits} holdTicks={_diagSink!.HoldTicks}" +
                   $"{DiagGlobalText()} watched={_diagCounters.Watched} ptrs={_diagCounters.Pointers} dropped={_diagClock.Dropped}";
        Array.Clear(_diagS, 0, _diagS.Length);
        return line;
    }

    /// <summary>" ctlUp=120/30 …" — every non-zero global slot as all hits / hits that named a watched monster.</summary>
    private string DiagGlobalText()
    {
        var s = "";
        foreach (DiagSlot slot in Enum.GetValues(typeof(DiagSlot)))
        {
            var all = _diagCounters!.Global(slot);
            if (all != 0) s += $" {SlotName(slot)}={all}/{_diagCounters.GlobalWatched(slot)}";
        }
        return s;
    }

    private void DiagSummary(long now)
    {
        var rows = _diagRows.Values.OrderByDescending(r => r.DrawnAnimating + r.ControllerAnimating + r.OffHold + (r.Untargeted ? 100 : 0)).ToList();
        foreach (var r in rows.Take(SummaryEntityLines)) _log.Info(DiagEntitySummary(r));
        var t = DiagTally(rows);
        _log.Info($"{DiagTag}summary: ms={_diagClock!.Elapsed(now)} samples={_diagClock.Samples} monsters={t.Monsters} " +
                  $"untargeted={t.Untargeted} neverFrozen={t.NeverFrozen} notHeld={t.NotHeld} untracked={t.Untracked} " +
                  $"drawnAnim={t.DrawnAnimating} ctlAnim={t.ControllerAnimating} offHold={t.OffHold} posPost={t.PositionAfterHold} " +
                  $"ctlUp={t.ControllerSpeedUp} sub={t.GateSubstituted} fxMissed={t.EffectsMissed} fxUnfrozen={t.EffectsUnfrozen} " +
                  $"despawns={t.Despawns} offThread={_diagCounters!.OffThreadHits} dropped={_diagClock.Dropped} | " +
                  $"verdict={string.Join(",", FreezeDiagVerdict.Explain(t))} | hooks: {FreezeDiagPatches.HitsText()}");
    }

    private string DiagEntitySummary(FreezeDiagRow r)
    {
        var c = _diagCounters!;
        var n = "";
        foreach (DiagSlot slot in Enum.GetValues(typeof(DiagSlot)))
            if (c.Total(r.Uuid, slot) is var v && v != 0) n += $" {SlotName(slot)}={v}";
        return $"{DiagTag}entity-summary u={r.Uuid} k={r.Kind} in0={r.FirstColls} samples={r.Samples} drawnAnim={r.DrawnAnimating} " +
               $"ctlAnim={r.ControllerAnimating} offHold={r.OffHold} maxOffHold={r.MaxOffHold:F2} untargeted={YN(r.Untargeted)} " +
               $"neverFrozen={YN(r.NeverFrozen)} notHeld={YN(r.NotHeld)} untracked={YN(r.Untracked)} swapped={YN(r.Swapped)} " +
               $"despawn={DespawnText(r.Uuid)} | n:{(n.Length == 0 ? " -" : n)}";
    }

    private FreezeDiagTally DiagTally(List<FreezeDiagRow> rows)
    {
        var c = _diagCounters!;
        var t = new FreezeDiagTally
        {
            Monsters = rows.Count,
            Untargeted = rows.Count(r => r.Untargeted),
            NeverFrozen = rows.Count(r => r.NeverFrozen),
            NotHeld = rows.Count(r => r.NotHeld),
            Untracked = rows.Count(r => r.Untracked),
            DrawnAnimating = rows.Sum(r => r.DrawnAnimating),
            ControllerAnimating = rows.Sum(r => r.ControllerAnimating),
            OffHold = rows.Sum(r => r.OffHold),
            PositionAfterHold = c.GlobalWatched(DiagSlot.GoPosAfter),
            ControllerSpeedUp = c.GlobalWatched(DiagSlot.CtlSpeedUp),
            GateSubstituted = c.GlobalWatched(DiagSlot.GateSub),
            EffectsMissed = _diagFxMissedMax,
            EffectsUnfrozen = _diagFxUnfrozenMax + c.Global(DiagSlot.FxUnfreeze),
            Despawns = _diagDespawns,
        };
        return t;
    }
}
