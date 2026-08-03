using System.Collections.Generic;
using Stellar.Abstractions.Domain.Inventory;

namespace Stellar.Abstractions.Domain.Loadout;

/// <summary>A saved in-game loadout entry: its identifier, display name, and whether it is currently active.</summary>
/// <param name="Index">Stable game-defined identifier passed to <see cref="Stellar.Abstractions.Services.ILoadout.ApplyAsync"/>. This is the game's loadout/project id, not necessarily a positional index; see the loadout recon findings.</param>
/// <param name="Name">Display name as shown in the in-game dropdown (e.g. "Ici-LF"), or a fallback like "Loadout N" if unresolved.</param>
/// <param name="IsCurrent">True if this loadout is the one currently applied.</param>
/// <param name="ProfessionId">The loadout's class/profession id, or 0 if unresolved.</param>
/// <param name="TalentStageId">The loadout's active talent-stage config id, or 0 if unresolved.</param>
/// <param name="TalentNodes">The actual allocated talent-tree node ids for this loadout's profession (from <c>professionList.talentList[professionId].talentNodeIds</c>), or null if unresolved.</param>
/// <param name="Gear">This loadout's PER-CLASS equipped gear pieces (self-only) with full rolls, resolved from the saved plan's <c>equipInfoMap</c> (slot→uuid) via the item container — NOT the class-blind live gear. Each <see cref="GearInstance"/> carries its own slot. Null until the item container resolves.</param>
/// <param name="Modules">This loadout's PER-CLASS equipped modules (self-only) with rolled parts, keyed by 1-based module slot, resolved from the saved plan's <c>modInfoMap</c> (slot→uuid) via the item container. Null until resolved.</param>
public sealed record LoadoutSlot(int Index, string Name, bool IsCurrent, int ProfessionId = 0, int TalentStageId = 0, IReadOnlyList<int>? TalentNodes = null, IReadOnlyList<GearInstance>? Gear = null, IReadOnlyDictionary<int, ModuleInfo>? Modules = null);
