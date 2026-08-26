using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Vitals write-site helpers for <see cref="PandaCombatStubProbe"/> — extracted from
/// PandaCombatStubProbe.Receive.cs (which sits at the file-size gate) to keep both files under it.
/// <see cref="ResolveMaxHp"/> and <see cref="MapDisappearReason"/> are the pure logic behind the
/// 2026-08-26 raid-bosshp-capture-design § decision 1 wire fixes (AttrMaxHpTotal=11321 acceptance +
/// EDisappearType parsing); both are <c>internal</c> specifically so they're unit-testable directly.
/// </summary>
internal sealed partial class PandaCombatStubProbe
{
    // Shared by BOTH vitals write sites (ReadAppearEntity + ApplyParsedDelta/ApplyAttrDeltasForEntity):
    // 11320 (AttrMaxHp) stays primary; 11321 (AttrMaxHpTotal) wins only when 11320 is absent from THIS
    // payload. internal (not private) so both write sites are provably equivalent, and so this pure
    // resolution rule is unit-testable directly without constructing the full probe.
    internal static long ResolveMaxHp(long maxHpBase, long maxHpTotal) => maxHpBase >= 0 ? maxHpBase : maxHpTotal;

    // Maps the wire's raw EDisappearType int (SyncNearEntitiesReader.DisappearEntityMsg.DisappearType)
    // to the domain enum. Anything other than the three named non-Normal values — including the proto's
    // own EDisappearTransferPassLineLeave=4 and any future addition — reports Unknown, which
    // CombatEntityTracker.OnEntityDisappeared treats identically to a real disappear (evict; safe default).
    // internal (not private) so this pure mapping is unit-testable directly.
    internal static EntityDisappearReason MapDisappearReason(int wireType) => wireType switch
    {
        0 => EntityDisappearReason.Normal,
        1 => EntityDisappearReason.Dead,
        2 => EntityDisappearReason.Destroy,
        3 => EntityDisappearReason.TransferLeave,
        _ => EntityDisappearReason.Unknown,
    };

    // Post-loop apply for ApplyAttrDeltasForEntity (PandaCombatStubProbe.Receive.cs) — extracted to
    // keep that parse loop under the 50-LoC gate (and this one under the 5-param gate:
    // hp/maxHpBase/maxHpTotal bundled into one tuple). 11320 stays primary; 11321 wins only when
    // 11320 is absent from THIS delta (decision 1).
    private void ApplyParsedDelta(EntityId eid, (long Hp, long MaxHpBase, long MaxHpTotal) vitals, long? teamId, long? fightPoint)
    {
        long maxHp = ResolveMaxHp(vitals.MaxHpBase, vitals.MaxHpTotal);
        if (vitals.Hp >= 0 || maxHp >= 0) _sink.UpdateEntityVitals(eid, vitals.Hp, maxHp);
        if (teamId is long t)             _sink.UpdateEntityTeamId(eid, t);
        if (fightPoint is long fp)
        {
            _sink.UpdateEntityFightPoint(eid, fp);
            _sink.SetEntityAttribute(eid, AttrTypeIds.AttrFightPoint, fp);
        }
        DiagBossHpWire(eid, "delta", vitals.Hp, vitals.MaxHpBase, vitals.MaxHpTotal);
    }
}
