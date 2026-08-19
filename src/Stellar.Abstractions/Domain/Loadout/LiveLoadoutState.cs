using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.Loadout;

/// <summary>The local player's LIVE loadout identity — the class they are actually on and its
/// currently-allocated talents, read from the game's live containers (never from a saved loadout
/// plan). Surfaced as <see cref="Stellar.Abstractions.Services.ILoadout.LiveState"/>; null until the
/// live read resolves in-world.</summary>
/// <param name="ProfessionId">The live class/profession id.</param>
/// <param name="TalentStageId">The live active talent-stage config id, or 0 if unresolved.</param>
/// <param name="TalentNodes">The live allocated talent-tree node ids, or null if unresolved.</param>
public sealed record LiveLoadoutState(int ProfessionId, int TalentStageId, IReadOnlyList<int>? TalentNodes);
