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

    // Fix round 1 (5): the raw action readback for the end-of-action gate decision — does a finished one-shot keep its id,
    // does passed run past total, does a loop's passed wrap? Logged on the live-person read (the panel poll and the
    // PhotoStudio posing self-test's probe=oneshot/probe=loop reads), at most every 200 ms so a 10 Hz panel poll is halved.
    private long _lastRawLog;

    partial void OnActionRead(long uuid, object? model)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _lastRawLog < Stopwatch.Frequency / 5) return;
        _lastRawLog = now;
        var ok = _calls.Models.ReadRaw(model, out var id, out var passed, out var total);
        _log.Info(ok ? $"[Posing] action-raw uuid={uuid} id={id:F0} passed={passed:F3} total={total:F3}"
            : $"[Posing] action-raw uuid={uuid} model=none");
    }

    private static double Ms(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
