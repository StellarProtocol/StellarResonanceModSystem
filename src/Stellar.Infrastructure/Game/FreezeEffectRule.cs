namespace Stellar.Infrastructure.Game;

/// <summary>Which NEW effect the freeze freezes (owner MAIN evidence 2026-10-02: a frozen boss's skill effects kept
/// playing — <c>fx … unfrozenNew&gt;0 owners=[1704000*:…]</c>; <c>ZEffect.Init</c> fired 7838 times in one freeze while the
/// hooked <c>AddEffectDisplay(ZEffect)</c> missed some, and <c>ZEffectManager.AddEffect</c> never fired). The OWNER does
/// not matter: scene-stays spec § 3 keeps effects frozen GLOBALLY, the local player's own new skill effects included
/// (accepted by the owner) — so a frozen monster's effect, a player's, a summon's and an unowned one all freeze. Only a
/// live freeze freezes, and an effect already touched is not frozen twice (it is in the ledger: unfrozen on release).
/// Pure (unit-tested).</summary>
internal static class FreezeEffectRule
{
    /// <param name="frozen">The scene freeze is on.</param>
    /// <param name="alreadyTouched">The effect's uid is already in the ledger.</param>
    /// <param name="owner">The effect's <c>EffectContext.BelongUuid</c> (0 = unknown) — deliberately ignored (§ 3).</param>
    /// <param name="self">The local player's uuid — deliberately ignored (§ 3).</param>
    public static bool ShouldFreeze(bool frozen, bool alreadyTouched, long owner, long self) => frozen && !alreadyTouched;
}
