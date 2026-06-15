using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.GameData;

/// <summary>One <c>EquipEnchantItemTable</c> row — a socketed gem at a specific level. Resolved from the
/// wire's <c>(enchant_item_type_id, enchant_level)</c> pair: the wire level is an internal progression
/// index, NOT the displayed level — the displayed level is baked into the gem ITEM's name
/// (<see cref="GemItemId"/> → item-table name, e.g. "Crimson Foxen Sigil Lv.2").</summary>
/// <param name="GemItemId">Item id of the gem at this level; its item-table name carries the display level.</param>
/// <param name="Effects">Flat stat grants of the gem (attr id + value), e.g. Haste +560. Never null.</param>
public readonly record struct EnchantItemInfo(int GemItemId, IReadOnlyList<EnchantEffect> Effects);

/// <summary>One flat stat grant on a gem: an attribute id and its (non-rolled) value.</summary>
/// <param name="AttrId">Attribute id (resolve name/format via <c>IGameDataCombat.GetAttribute</c>).</param>
/// <param name="Value">The granted value (flat — gems don't roll).</param>
public readonly record struct EnchantEffect(int AttrId, int Value);
