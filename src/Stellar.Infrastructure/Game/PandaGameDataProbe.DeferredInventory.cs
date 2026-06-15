using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // Deferred Inventory-domain projections — Equip + Weapon. WeaponKind enum
    // mapping lives alongside the other enum maps in
    // PandaGameDataProbe.EnumMaps.cs.

    // ===== Equip ==========================================================

    /// <summary>
    /// <c>Bokura.EquipEnchantItemTableBase</c> — equipment/enchant metadata.
    /// Spec §4 picks this as the equip-slot meta source from the several equip
    /// tables present. POCO has <c>Id, Name, Slot, BaseAttrs</c>.
    /// </summary>
    private IReadOnlyDictionary<int, EquipInfo> LoadEquips()
    {
        return LoadDeferredTable<EquipInfo>(
            label: "Equip",
            typeName: "Bokura.EquipEnchantItemTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");
                var slot = ReadInt(row, rowType, "Slot");
                var baseAttrs = ReadInt32Array(row, rowType, "BaseAttrs");

                return (id, new EquipInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Slot: slot,
                    BaseAttrs: baseAttrs));
            });
    }

    // ===== Weapon =========================================================

    /// <summary>
    /// <c>Bokura.EquipWeaponTableBase</c> — equippable-weapon roster on this
    /// build. (Spec's <c>Bokura.WeaponTableBase</c> does not exist; recon of
    /// Panda.Table.dll showed <c>EquipWeaponTableBase</c> is the canonical
    /// weapon-id→name row source.) Schema: <c>Id, Name, ProfessionId,
    /// WeaponSkinId</c>. The build has no per-weapon Kind/BaseDamage columns —
    /// <see cref="WeaponInfo.Kind"/> falls back to <see cref="WeaponKind.Unknown"/>
    /// and <see cref="WeaponInfo.BaseDamage"/> stays at 0 until a richer table is
    /// identified in Phase 7.
    /// </summary>
    private IReadOnlyDictionary<int, WeaponInfo> LoadWeapons()
    {
        return LoadDeferredTable<WeaponInfo>(
            label: "Weapon",
            typeName: "Bokura.EquipWeaponTableBase",
            capacityHint: 256,
            projector: (row, rowType) =>
            {
                var id = ReadInt(row, rowType, "Id");
                if (id == 0) return (0, default);

                var name = ReadStringOrMlString(row, rowType, "Name");

                return (id, new WeaponInfo(
                    Id: id,
                    Name: name ?? string.Empty,
                    Kind: WeaponKind.Unknown,
                    BaseDamage: 0));
            });
    }
}
