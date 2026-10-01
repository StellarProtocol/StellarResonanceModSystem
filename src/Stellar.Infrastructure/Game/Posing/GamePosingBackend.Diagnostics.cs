using System;
using System.Diagnostics;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Per-open diagnostics (STELLAR_DIAGNOSTICS=1): which person, how long the copy took (run 4: ~2 ms) and how
/// long an NPC model took to load (run 5: 240–303 ms, ~3 ms cached).</summary>
internal sealed partial class GamePosingBackend
{
    partial void OnOpened(PersonKind kind, long uuid, long started)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Posing] open kind={kind} uuid={uuid} in {Ms(started):F1} ms");
    }

    partial void OnNpcLoaded(long uuid, bool ok, long started)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Posing] npc model uuid={uuid} loaded={ok} in {Ms(started):F0} ms");
    }

    // Review F4: proves which thread Harmony runs the ZEntityMgr.RemoveEntity prefix on (expected: the main thread,
    // same as every other posing call) — an in-game smoke with STELLAR_DIAGNOSTICS=1 checks this line against
    // Environment.CurrentManagedThreadId logged at Load() (Wiring.Posing.cs).
    partial void OnDespawnPrefixFired()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Posing] despawn prefix fired on managed thread {Environment.CurrentManagedThreadId}");
    }

    private static double Ms(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
