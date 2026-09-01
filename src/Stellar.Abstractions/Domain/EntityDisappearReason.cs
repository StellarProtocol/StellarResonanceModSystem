namespace Stellar.Abstractions.Domain;

/// <summary>
/// Subset of the wire's <c>zproto.EDisappearType</c> (see
/// <c>data/StarResonanceData/proto/zproto/enum_e_disappear_type.proto</c> and
/// <c>DisappearEntity.Type</c>, field 2, on <c>SyncNearEntities</c>'s disappear list) surfaced to
/// the framework-internal combat event sink's <c>OnEntityDisappeared(EntityId, EntityDisappearReason)</c>.
/// Distinguishes "left AOI while still alive elsewhere" from a real death/destroy/transfer — see
/// <c>docs/superpowers/specs/2026-08-26-raid-bosshp-capture-design.md</c> § decision 1 (the L1 fix:
/// a raid boss walking out of AOI mid-fight used to evict its vitals row unconditionally, starving
/// the HP sampler until the boss re-entered AOI).
///
/// <para>Named-value numbers are the proto's own wire ints, so a caller comparing against the raw
/// wire value never needs a separate lookup table — same tolerance policy as
/// <see cref="ActorState"/>/<see cref="PartyLeaveKind"/>: any wire value this framework doesn't name
/// (including a future addition, e.g. the proto's own <c>EDisappearTransferPassLineLeave</c>=4) reports
/// as <see cref="Unknown"/> rather than throwing, and is treated exactly like a real disappear
/// (evict) — the safe default.</para>
/// </summary>
public enum EntityDisappearReason
{
    /// <summary>
    /// Wire value not one of the reasons named below, OR no disappear-type context is available at
    /// all (e.g. a non-wire caller such as the idle-entity sweep). Treated as a real disappear —
    /// evicts cached state exactly like <see cref="Dead"/>/<see cref="Destroy"/>/<see cref="TransferLeave"/>.
    /// This is the default parameter value on <c>OnEntityDisappeared</c> so every caller that predates
    /// this fix keeps today's evict-everything behavior unchanged.
    /// </summary>
    Unknown = -1,

    /// <summary>
    /// <c>EDisappearNormal</c> (0) — the entity left AOI while still alive elsewhere (the common case
    /// on a raid's one big multi-stage map). Vitals + the raw attr map are KEPT rather than evicted —
    /// stale-but-known beats <c>Unknown</c>, which is what starved the boss-HP sampler (L1).
    /// </summary>
    Normal = 0,

    /// <summary><c>EDisappearDead</c> (1) — the entity died.</summary>
    Dead = 1,

    /// <summary><c>EDisappearDestroy</c> (2) — the entity was destroyed/despawned.</summary>
    Destroy = 2,

    /// <summary><c>EDisappearTransferLeave</c> (3) — the entity transferred to another scene/instance.</summary>
    TransferLeave = 3,
}
