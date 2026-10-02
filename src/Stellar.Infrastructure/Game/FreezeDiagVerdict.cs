using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>What the combat-freeze sampler saw over one freeze, summed over the watched monsters / bosses (diagnostics
/// only). Sample counts are "entity-samples": one per entity per sample.</summary>
internal sealed class FreezeDiagTally
{
    /// <summary>Distinct monsters / bosses sampled.</summary>
    public int Monsters { get; set; }
    /// <summary>Sampled monsters that were in neither the press's target list nor the appear watch.</summary>
    public int Untargeted { get; set; }
    /// <summary>Sampled monsters the freeze targeted but never gave a stage-1 factor nor a stage-2 drawn speed.</summary>
    public int NeverFrozen { get; set; }
    /// <summary>Sampled monsters with no position hold entry.</summary>
    public int NotHeld { get; set; }
    /// <summary>Sampled monsters whose anim component the set_Speed gate does not track.</summary>
    public int Untracked { get; set; }
    /// <summary>Entity-samples with the drawn <c>AnimComp.Speed</c> above 0.</summary>
    public int DrawnAnimating { get; set; }
    /// <summary>Entity-samples with the drawn speed at 0 but the anim CONTROLLER's speed above 0.</summary>
    public int ControllerAnimating { get; set; }
    /// <summary>Entity-samples whose drawn position (read before our hold write) was off the held position.</summary>
    public int OffHold { get; set; }
    /// <summary>Game writes of a watched monster's drawn position after our hold write in the same frame.</summary>
    public int PositionAfterHold { get; set; }
    /// <summary>Direct controller speed writes above 0 on watched monsters.</summary>
    public int ControllerSpeedUp { get; set; }
    /// <summary>set_Speed gate substitutions on watched monsters.</summary>
    public int GateSubstituted { get; set; }
    /// <summary>Most effects seen unfrozen in one census that the freeze never touched (created by an unhooked path).</summary>
    public int EffectsMissed { get; set; }
    /// <summary>Most effects seen unfrozen in one census that the freeze DID touch (the game un-froze them), plus every
    /// game <c>SetEffectFreeze(false)</c> call.</summary>
    public int EffectsUnfrozen { get; set; }
    /// <summary>Frozen entities removed while frozen.</summary>
    public int Despawns { get; set; }
}

/// <summary>Turns a <see cref="FreezeDiagTally"/> into the hypothesis tags printed on the <c>[FreeCamDiag] summary:</c>
/// line — one tag per hypothesis the data supports, in a fixed order. Pure (unit-tested).</summary>
internal static class FreezeDiagVerdict
{
    public static IReadOnlyList<string> Explain(FreezeDiagTally t)
    {
        var tags = new List<string>();
        if (t.Monsters == 0) { tags.Add("NO-MONSTERS-SAMPLED"); return tags; }
        if (t.Untargeted > 0) tags.Add("NOT-TARGETED");                 // the press / appear never saw them
        if (t.NeverFrozen > 0) tags.Add("TARGETED-NOT-FROZEN");         // seen, but stage 1 and 2 both skipped them
        if (t.Untracked > 0) tags.Add("COMP-UNTRACKED");                // the set_Speed gate cannot hold them
        if (t.DrawnAnimating > 0) tags.Add("DRAWN-SPEED-RESUMED");      // a set_Speed path the gate missed (inlined?)
        if (t.ControllerAnimating > 0 || t.ControllerSpeedUp > 0) tags.Add("ANIM-OTHER-DRIVER");   // controller written directly
        if (t.NotHeld > 0) tags.Add("HOLD-NOT-COVERING");
        if (t.PositionAfterHold > 0) tags.Add("POSITION-WRITTEN-AFTER-HOLD");
        else if (t.OffHold > 0) tags.Add("POSITION-MOVER-BETWEEN-HOLDS");
        if (t.EffectsMissed > 0) tags.Add("FX-CREATED-UNHOOKED");
        if (t.EffectsUnfrozen > 0) tags.Add("FX-UNFROZEN-BY-GAME");
        if (t.Despawns > 0) tags.Add("DESPAWNED-WHILE-FROZEN");
        if (tags.Count == 0) tags.Add("ALL-HELD");
        return tags;
    }
}
