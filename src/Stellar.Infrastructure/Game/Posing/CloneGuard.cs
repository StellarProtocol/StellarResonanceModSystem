namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// Guard against the game's own photo-copy crash (regression id <c>clone-nre-male-null-ridetpl</c>; root cause measured
/// in probe run 6, 2026-10-02, devkit <c>.superpowers/sdd/posing/clone-nre-rootcause.md</c>). Inside
/// <c>ZAnimActionPlayMgr.CloneModelForPhoto</c>, when the source is NOT (<c>GetAttrState == ActorStateAction (8)</c> and
/// <c>GetAttrActionInfoActionId &gt; 0</c>), the game copies the source's ride template onto the copy with
/// <c>EntityAttrExtensions.SetAttrAnimRideTemplate</c>, which for a Male model (<c>ModelGender == EM (1)</c>) calls
/// <c>addr.Replace(…)</c> with no null check — so an idle Male player who never had a ride template (null) throws a
/// NullReferenceException and leaves a registered, never-loaded copy behind. Female sources never take the Replace
/// path; an acting source skips the template copy. Pure (unit-tested).
/// </summary>
internal static class CloneGuard
{
    /// <summary><c>Panda.ZGame.EModelGender.EM</c> (Unknown 0, EM 1, EF 2).</summary>
    internal const int GenderMale = 1;

    /// <summary><c>EActorState.ActorStateAction</c> — the clone callback's "replay the action" branch.</summary>
    internal const int StateAction = 8;

    /// <summary>True when the copy would crash: a Male source, not acting (state 8 with an action id), and a null ride
    /// template. The fix sets the source's template to <c>""</c> (a value the game itself produces) before the copy.</summary>
    public static bool NeedsRideTemplateNormalise(int gender, int state, int actionId, bool templateIsNull) =>
        gender == GenderMale && templateIsNull && !(state == StateAction && actionId > 0);
}
