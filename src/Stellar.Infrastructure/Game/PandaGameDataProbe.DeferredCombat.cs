using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // Deferred Combat-domain projections — Talent + DamageAttr. Each goes
    // through the shared LoadDeferredTable<TInfo> envelope so the success log
    // line shape ("[Stellar][GameData] deferred: <Label> loaded (N rows, Mms)")
    // is consistent with eager loads.
    //
    // Field names below are pinned by the in-game ReadProxy.ReadMLString /
    // reflection dumps; unknown fields fall through ReadString helpers and
    // produce empty/zero values rather than throwing.

    // ===== Talent =========================================================

    /// <summary>
    /// <c>Bokura.TalentTableBase</c> — primary talent definitions (one row per
    /// talent id). 14 talent-related tables exist; the rest are stage /
    /// progression sub-tables out of scope for v1.
    /// </summary>
    private IReadOnlyDictionary<int, TalentInfo> LoadTalents()
    {
        return LoadDeferredTable<TalentInfo>(
            label: "Talent",
            typeName: "Bokura.TalentTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var desc = ReadStringOrMlString(row, rowType, "Description");
                if (string.IsNullOrEmpty(desc))
                {
                    desc = ReadStringOrMlString(row, rowType, "Desc");
                }
                var iconPath = ReadString(row, rowType, "IconPath");
                var professionId = ReadInt(row, rowType, "ProfessionId");

                return (id, new TalentInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Description: desc ?? string.Empty,
                    IconPath: iconPath ?? string.Empty,
                    ProfessionId: professionId));
            });
    }

    // ===== DamageAttr =====================================================

    /// <summary>
    /// <c>Bokura.DamageAttrTableBase</c> — damage attribute (element/physical)
    /// metadata. Fields per spec §4: <c>Id, Name, ElementKind, BaseValue</c>.
    /// </summary>
    private IReadOnlyDictionary<int, DamageAttrInfo> LoadDamageAttrs()
    {
        return LoadDeferredTable<DamageAttrInfo>(
            label: "DamageAttr",
            typeName: "Bokura.DamageAttrTableBase",
            capacityHint: 32,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var elementKind = ReadInt(row, rowType, "ElementKind");
                var baseValue = ReadInt(row, rowType, "BaseValue");

                return (id, new DamageAttrInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    ElementKind: elementKind,
                    BaseValue: baseValue));
            });
    }
}
