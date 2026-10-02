using System;
namespace Stellar.Infrastructure.Game;

/// <summary>The unfreeze steps, in the order <see cref="FreezeTeardown.Run"/> calls them (implemented by
/// <see cref="GameFreezeBackend"/>; faked in unit tests).</summary>
internal interface IFreezeTeardownSteps
{
    /// <summary>Disarms the <c>set_Speed</c> gate and the ECS layer gate — from here every write passes through, ours included.</summary>
    void DisarmGate();

    /// <summary>Releases the position hold (held models snapped to their logical position).</summary>
    void StopHold();

    /// <summary>Writes each entity's kept drawn speed back.</summary>
    void RestoreDrawnSpeeds();

    /// <summary>Writes each entity's first factor prior back (players' drawn speed is recomputed from it).</summary>
    void RestoreFactors();

    /// <summary>Writes each ECS model's animation layers back: the whole model at its controller's (now restored) speed,
    /// then each layer's latest wished speed (combat-freeze fix, owner MAIN evidence 2026-10-02).</summary>
    void RestoreEcsLayers();

    /// <summary>Unfreezes the effects this freeze touched.</summary>
    void UnfreezeEffects();

    /// <summary>Forgets everything this freeze kept.</summary>
    void ClearLedger();
}

/// <summary>The unfreeze ORDER, kept pure so it is pinned (review, regression <c>freeze_unfreeze_order_*</c>): the gate is
/// disarmed BEFORE any restore write (an armed gate would turn the restore itself into 0), and drawn speeds are restored
/// BEFORE factors (a factor restore recomputes a player's drawn speed; the other order would overwrite that with a stale
/// kept speed). The ECS layers are restored AFTER both (combat-freeze fix 2026-10-02): their whole-model write reads the
/// controller's speed, which only the drawn-speed and factor restores put back. The disarm runs first and alone — it cannot fail; every later step runs through
/// <see cref="FreezeStepRunner"/>, so one throwing step never skips the rest. Returns the first exception.</summary>
internal static class FreezeTeardown
{
    public static Exception? Run(IFreezeTeardownSteps steps)
    {
        steps.DisarmGate();
        return FreezeStepRunner.RunAll(steps.StopHold, steps.RestoreDrawnSpeeds, steps.RestoreFactors, steps.RestoreEcsLayers,
            steps.UnfreezeEffects, steps.ClearLedger);
    }
}
