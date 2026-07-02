namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — Application asks Infrastructure to resolve a live
/// monster entity's config/template id so callers can look up
/// <c>MonsterTableBase</c> rows (e.g. <c>MonsterRank</c>, boss classification).
///
/// <para>
/// The config id is a per-type stable integer that keys the
/// <c>Bokura.MonsterTableBase</c> row for this monster species. It is distinct
/// from the runtime entity uuid (<see cref="Stellar.Abstractions.Domain.EntityId"/>),
/// which identifies a single live instance.
/// </para>
///
/// <para>
/// Implemented in <c>Stellar.Infrastructure</c> by
/// <c>MonsterCatalogService</c>. Returns <c>null</c> until the in-game recon
/// spike (Task 1) confirms which field carries the config id.
/// </para>
/// </summary>
internal interface IMonsterCatalog
{
    /// <summary>
    /// Returns the monster config/template id for the entity identified by
    /// <paramref name="entityUuid"/>, or <c>null</c> when:
    /// <list type="bullet">
    ///   <item>the entity is not currently in the AOI (already despawned),</item>
    ///   <item>the config-id source has not yet been confirmed by the recon spike, or</item>
    ///   <item>an IL2CPP reflection call failed.</item>
    /// </list>
    /// The returned id is the <c>Bokura.MonsterTableBase</c> row key usable for
    /// boss-classification lookups.
    /// </summary>
    /// <param name="entityUuid">
    /// Raw int64 entity uuid (the value behind
    /// <see cref="Stellar.Abstractions.Domain.EntityId.Value"/>).
    /// </param>
    /// <returns>Config id integer, or <c>null</c> on any miss.</returns>
    int? TryGetMonsterConfigId(long entityUuid);
}
