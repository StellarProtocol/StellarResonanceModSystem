using Stellar.Abstractions.Diagnostics;

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
}
