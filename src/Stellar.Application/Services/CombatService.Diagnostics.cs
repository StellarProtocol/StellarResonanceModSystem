using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Opt-in idle-sweep diagnostics for <see cref="CombatService"/>. Gated on
/// <c>STELLAR_DIAGNOSTICS=1</c> so steady-state <c>Drain</c> pays zero cost.
/// Logged only when a sweep actually evicted entities — confirms the Task 3
/// FPS cache-leak fix is doing work over a long session, without adding a log
/// line to every no-op sweep tick.
/// </summary>
internal sealed partial class CombatService
{
    private void LogIdleSweep(int evicted)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (evicted <= 0) return;

        _log.Info($"[Combat.Diag] idle sweep evicted={evicted} non-player entities (ttlMs={IdleEntityTtlMs})");
    }

    // Probe line for the rDPS capture spec (§ 7 checks 1 and 2; check 5 is read from the
    // plugin's uploaded dmg+buff event stream, not this log): one line per buff change on
    // a PLAYER target. Volume ≈ tens/s in a 5-player dungeon — diagnostics-only by construction.
    private void DiagBuffChange(string kind, EntityId target, ActiveBuff b, long timestampMs)
    {
        if (!StellarDiagnostics.IsEnabled || !target.IsPlayer) return;
        _log.Info($"[Buff] {kind} base={b.BaseId} lvl={b.Level} tgt={target.Value} firer={b.FirerId.Value} " +
                  $"srcKind={b.SourceKind} srcId={b.SourceId} stacks={b.Stacks} dur={b.DurationMs} ms={timestampMs}");
    }
}
