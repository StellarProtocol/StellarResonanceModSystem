using System.Collections.Generic;

namespace Stellar.Abstractions.Domain.DeepSlumber;

/// <summary>One area (node cluster) of a Deep-Slumber Psychoscope cultivate line: its activation
/// state, effect score, and the player's node allocations — socketed boss-soul cards on big nodes,
/// socketed items on middle nodes, and leveled normal nodes.</summary>
/// <param name="AreaId">The area's id within its line (game map key).</param>
/// <param name="IsActive">True when the player has this area enabled.</param>
/// <param name="Score">The area's activate-effect score as the game reports it.</param>
/// <param name="BigNodes">[nodeId, fantasyCardId] pairs — socketed psycho (boss-soul) cards.</param>
/// <param name="MiddleNodes">[nodeId, itemId] pairs — socketed items.</param>
/// <param name="NormalNodes">[nodeId, activeLevel] pairs — leveled small nodes.</param>
public sealed record DeepSlumberArea(
    int AreaId,
    bool IsActive,
    long Score,
    IReadOnlyList<int[]> BigNodes,
    IReadOnlyList<int[]> MiddleNodes,
    IReadOnlyList<int[]> NormalNodes);

/// <summary>One Deep-Slumber cultivate line variant: the line id, its sub-type, and its areas.</summary>
/// <param name="LineId">The season cultivate line id (map key in the game container).</param>
/// <param name="SubType">The line's sub-type id (inner map key).</param>
/// <param name="Areas">The line's areas in container order.</param>
public sealed record DeepSlumberLine(int LineId, int SubType, IReadOnlyList<DeepSlumberArea> Areas);

/// <summary>The local player's full live Deep-Slumber Psychoscope (season cultivate) state.</summary>
/// <param name="SeasonLevels">[seasonId, psychoscope level] pairs from the season role-level map.</param>
/// <param name="Lines">Every cultivate line variant present in the live container.</param>
public sealed record DeepSlumberState(
    IReadOnlyList<int[]> SeasonLevels,
    IReadOnlyList<DeepSlumberLine> Lines);
