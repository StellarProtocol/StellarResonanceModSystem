using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Freeze review round 2 (qa minor): the unfreeze ORDER was unpinned. The gate must be disarmed before any restore write
// (else the restore itself is substituted to 0), and drawn speeds restored before factors (a factor restore recomputes
// a player's drawn speed). Pinned through the pure FreezeTeardown with a recording fake. Do not weaken.
public sealed class FreezeTeardownTests
{
    [Fact]
    public void freeze_unfreeze_order_disarms_first_and_restores_speeds_before_factors()
    {
        var steps = new Recorder();
        Assert.Null(FreezeTeardown.Run(steps));
        Assert.Equal(new[] { "DisarmGate", "StopHold", "RestoreDrawnSpeeds", "RestoreFactors", "UnfreezeEffects", "ClearLedger" }, steps.Calls);
    }

    [Fact]
    public void freeze_unfreeze_order_a_failing_step_never_skips_the_rest()
    {
        var steps = new Recorder { Throw = "RestoreDrawnSpeeds" };
        Assert.IsType<InvalidOperationException>(FreezeTeardown.Run(steps));
        Assert.Equal(new[] { "DisarmGate", "StopHold", "RestoreDrawnSpeeds", "RestoreFactors", "UnfreezeEffects", "ClearLedger" }, steps.Calls);
    }

    private sealed class Recorder : IFreezeTeardownSteps
    {
        public readonly List<string> Calls = new();
        public string? Throw;

        public void DisarmGate() => Note(nameof(DisarmGate));
        public void StopHold() => Note(nameof(StopHold));
        public void RestoreDrawnSpeeds() => Note(nameof(RestoreDrawnSpeeds));
        public void RestoreFactors() => Note(nameof(RestoreFactors));
        public void UnfreezeEffects() => Note(nameof(UnfreezeEffects));
        public void ClearLedger() => Note(nameof(ClearLedger));

        private void Note(string step)
        {
            Calls.Add(step);
            if (step == Throw) throw new InvalidOperationException(step);
        }
    }
}
