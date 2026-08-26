using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Native boss-HP tap: reads the SAME merged per-entity store the game's own boss bar reads
/// (<c>Panda.ZUi.BossBloodUtil.ConversionBloodLogicDataToViewData(ZEntity)</c>) instead of the combat
/// wire's <c>AttrCollection</c> mirror (<see cref="ICombatLookup.GetVitals"/>). Immune by construction
/// to the wire mirror's AOI-eviction starvation (a raid boss that leaves your AOI mid-fight on a raid's
/// one big multi-stage map) — see <c>docs/superpowers/specs/2026-08-26-raid-bosshp-capture-design.md</c>
/// § decision 2. Backed by the game's entity manager; reads MUST happen on the main thread (the
/// framework <see cref="IFramework.Update"/> tick), same contract as <see cref="IEntityTransforms"/>.
/// Returns <c>false</c> when the entity isn't resolvable this frame (despawned / not loaded / no
/// game) or has never reported a blood/boss observation — callers should treat that as "unknown", not
/// "zero", and fall back to <see cref="ICombatLookup.GetVitals"/>.
/// </summary>
public interface IBossVitals
{
    /// <summary>
    /// Attempts to read the current boss-blood percentage and stage for the entity identified by
    /// <paramref name="id"/>, byte-identical to what the game's own boss bar renders. Returns
    /// <c>false</c> when unresolvable or never observed; in that case <paramref name="percent"/> and
    /// <paramref name="stage"/> are both 0.
    /// </summary>
    /// <param name="id">The entity id (as seen on combat events / the boss detection surface).</param>
    /// <param name="percent">Blood percentage in [0,100] on success.</param>
    /// <param name="stage">Boss blood-bar stage (<c>BossBloodUtil.CalculateBloodStage</c>) on success.</param>
    /// <returns><c>true</c> if a live boss-blood observation was read; otherwise <c>false</c>.</returns>
    bool TryGetBlood(EntityId id, out int percent, out int stage);

    /// <summary>
    /// Whether the game's own client currently flags this entity as a boss
    /// (<c>ZEntity.IsBoss</c>) — the same flag <c>bossbattle_view.lua</c> gates on. Returns
    /// <c>false</c> when the entity isn't resolvable this frame, which is indistinguishable from
    /// "resolvable and not a boss" — callers that need to tell the two apart should corroborate with
    /// <see cref="TryGetBlood"/> or another liveness signal.
    /// </summary>
    /// <param name="id">The entity id to check.</param>
    bool IsBoss(EntityId id);
}
