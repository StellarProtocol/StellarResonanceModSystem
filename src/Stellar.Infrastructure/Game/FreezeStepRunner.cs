using System;
namespace Stellar.Infrastructure.Game;

/// <summary>Runs a fixed sequence of steps, isolating each from the others' failures: every step always runs even
/// when an earlier one throws, so a caller that needs every step to execute regardless of individual failures (a
/// freeze/unfreeze sequence, say) ends in a consistent state. Never re-throws — the whole point is that no single
/// step aborts the run — but returns the first exception caught so the caller can report it. Pure and IL2CPP-free
/// so it is testable without the game; used by <see cref="GameFreezeBackend"/>'s <c>FreezeAll</c>/<c>UnfreezeAll</c>
/// (the "a failure in one never stops the others" contract).</summary>
internal static class FreezeStepRunner
{
    public static Exception? RunAll(params Action[] steps)
    {
        Exception? first = null;
        foreach (var step in steps)
        {
            try { step(); }
            catch (Exception ex) { first ??= ex; }
        }
        return first;
    }
}
