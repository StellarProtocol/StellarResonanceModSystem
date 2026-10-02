using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>Which NEW effect the freeze freezes (owner MAIN evidence 2026-10-02: a frozen boss's skill effects kept
/// playing — <c>fx … unfrozenNew&gt;0 owners=[1704000*:…]</c>; <c>ZEffect.Init</c> fired 7838 times in one freeze while the
/// hooked <c>AddEffectDisplay(ZEffect)</c> missed some, and <c>ZEffectManager.AddEffect</c> never fired). The OWNER does
/// not matter: scene-stays spec § 3 keeps effects frozen GLOBALLY, the local player's own new skill effects included
/// (accepted by the owner) — so a frozen monster's effect, a player's, a summon's and an unowned one all freeze. Only a
/// live freeze freezes, and an effect already touched is not frozen twice (it is in the ledger: unfrozen on release).
/// <para><b>Release</b> (<see cref="Unfreeze"/>, review 2026-10-02): an effect frozen through its own instance
/// (<c>ZEffect.Init</c>, whose callers include <c>PathEffectInfo.Show</c> and a <c>ZEntityMgr</c> lambda — never added to
/// <c>EffectDict</c>) is invisible to the manager's <c>SetEffectFreeze(uid, false)</c>. A ledger uid found in
/// <c>EffectDict</c> is unfrozen through the manager, as before; one missing from it through the instance kept at freeze
/// time, but ONLY when that instance still carries the same uid and is not being destroyed (a pooled effect may have been
/// recycled for another one); otherwise it is never touched.</para>
/// Pure (unit-tested).</summary>
internal static class FreezeEffectRule
{
    /// <summary>How one ledger uid is unfrozen.</summary>
    internal enum UnfreezeRoute { Manager, Instance, Stale, Lost }

    /// <summary>What the release did: by the manager, by the kept instance, skipped as recycled / destroyed (stale), or
    /// skipped with no instance kept (lost). <see cref="MissingFromDict"/> = ledger uids not in <c>EffectDict</c>.</summary>
    internal readonly record struct UnfreezeCounts(int Manager, int Instance, int Stale, int Lost)
    {
        public int MissingFromDict => Instance + Stale + Lost;
    }

    /// <param name="inEffectDict">The uid is a key of <c>EffectDict</c> now (or the listing failed: the manager, as before).</param>
    /// <param name="instance">The instance kept at freeze time, read now: its uid and whether it is being destroyed (null =
    /// none kept).</param>
    /// <param name="uid">The ledger uid.</param>
    public static UnfreezeRoute Route(bool inEffectDict, (long Uid, bool Destroyed)? instance, long uid)
    {
        if (inEffectDict) return UnfreezeRoute.Manager;
        if (instance is not { } fx) return UnfreezeRoute.Lost;
        return fx.Uid == uid && !fx.Destroyed ? UnfreezeRoute.Instance : UnfreezeRoute.Stale;
    }

    /// <summary>Unfreezes every <paramref name="ledger"/> uid by its <see cref="Route"/>. <paramref name="inDict"/> null =
    /// the dictionary could not be listed (every uid through the manager). The instance is read only for a uid missing
    /// from the dictionary. One failing uid never skips the rest.</summary>
    public static UnfreezeCounts Unfreeze(IEnumerable<long> ledger, ICollection<long>? inDict,
        Func<long, (long Uid, bool Destroyed)?> instance, Action<long> byManager, Action<long> byInstance)
    {
        int manager = 0, byFx = 0, stale = 0, lost = 0;
        foreach (var uid in ledger)
        {
            try
            {
                var inside = inDict is null || inDict.Contains(uid);
                switch (Route(inside, inside ? null : instance(uid), uid))
                {
                    case UnfreezeRoute.Manager: byManager(uid); manager++; break;
                    case UnfreezeRoute.Instance: byInstance(uid); byFx++; break;
                    case UnfreezeRoute.Stale: stale++; break;
                    default: lost++; break;
                }
            }
            catch { stale++; }   // unreadable: counted, never retried
        }
        return new UnfreezeCounts(manager, byFx, stale, lost);
    }

    /// <param name="frozen">The scene freeze is on.</param>
    /// <param name="alreadyTouched">The effect's uid is already in the ledger.</param>
    /// <param name="owner">The effect's <c>EffectContext.BelongUuid</c> (0 = unknown) — deliberately ignored (§ 3).</param>
    /// <param name="self">The local player's uuid — deliberately ignored (§ 3).</param>
    public static bool ShouldFreeze(bool frozen, bool alreadyTouched, long owner, long self) => frozen && !alreadyTouched;
}
