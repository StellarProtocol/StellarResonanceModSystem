using System.Collections.Generic;
using System.Text;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Always-on one-shot diagnostic for <see cref="MonsterCatalogService"/>.
///
/// <para>
/// For the first <see cref="MonsterDumpMax"/> monster entities that appear in
/// AOI (as reported by <c>PandaCombatStubProbe.OnNearEntities</c>), logs one
/// line per entity:
/// <code>
/// [BossRecon] uuid=&lt;u&gt; uid=&lt;uid&gt; attrIds=[&lt;ids&gt;] attrVals=[&lt;vals&gt;] zentityConfigId=&lt;?&gt;
/// </code>
/// The line surfaces every <c>AttrCollection</c> attr id + int64-decoded value
/// AND a best-effort read of any config/monster id discoverable on the resolved
/// <c>ZEntity</c>/<c>ZModel</c>. Together these give the in-game confirmation
/// needed to implement <see cref="IMonsterCatalog.TryGetMonsterConfigId"/>.
/// </para>
///
/// <para>
/// This diagnostic is <b>always-on</b> (like
/// <c>PandaCombatStubProbe.DiagEnterSceneStructure</c>) — it fires regardless
/// of <c>STELLAR_DIAGNOSTICS</c>. The cap (<see cref="MonsterDumpMax"/>) limits
/// log volume to 8 lines per session.
/// </para>
/// </summary>
internal sealed partial class MonsterCatalogService
{
    private int _monsterDumps;
    private const int MonsterDumpMax = 8;

    /// <summary>
    /// Called by <c>PandaCombatStubProbe</c> for each appearing monster entity
    /// (i.e. <c>EntityId.IsMonster == true</c>) while the one-shot cap has not
    /// been reached. Logs one <c>[BossRecon]</c> line with all attr ids/values
    /// AND any config id readable from the live <c>ZEntity</c>.
    ///
    /// <para>Must be called on the network receive / dispatch thread (same thread
    /// as <c>OnNearEntities</c>). The ZEntity resolve path is main-thread-safe in
    /// practice because the entity is in-AOI, but is wrapped defensively.</para>
    /// </summary>
    /// <param name="uuid">Raw entity uuid.</param>
    /// <param name="attrs">
    /// All attrs from the entity's <c>AttrCollection</c>: (attr_id, int64_value).
    /// Pass an empty list if the entity had no <c>AttrCollection</c>.
    /// </param>
    internal void DiagMonster(long uuid, IReadOnlyList<(int id, long val)> attrs)
    {
        if (_monsterDumps >= MonsterDumpMax) return;
        _monsterDumps++;

        var zEntityConfigId = TryReadZEntityConfigId(uuid);

        var ids  = BuildAttrList(attrs, a => a.id.ToString());
        var vals = BuildAttrList(attrs, a => a.val.ToString());

        _log.Info(
            $"[BossRecon] uuid={uuid} uid={uuid >> 16} " +
            $"attrIds=[{ids}] attrVals=[{vals}] " +
            $"zentityConfigId={zEntityConfigId?.ToString() ?? "?"}");
    }

    // Attempts to resolve the ZEntity for the given uuid and extract a
    // config-id from it. Fully defensive — any failure returns null.
    private long? TryReadZEntityConfigId(long uuid)
    {
        try
        {
            EnsureHandlesResolved();
            var entity = ResolveEntity(uuid);
            if (entity is null) return null;

            // Try direct ZEntity config-id candidates first.
            var fromEntity = TryReadConfigIdFromEntity(entity);
            if (fromEntity.HasValue) return fromEntity;

            // Also probe the ZModel in case config id lives there.
            var model = ReadModel(entity);
            if (model is null) return null;
            return TryReadConfigIdFromEntity(model);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildAttrList<T>(
        IReadOnlyList<T> source,
        System.Func<T, string> selector)
    {
        if (source.Count == 0) return string.Empty;
        var sb = new StringBuilder(source.Count * 8);
        for (int i = 0; i < source.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(selector(source[i]));
        }
        return sb.ToString();
    }
}
