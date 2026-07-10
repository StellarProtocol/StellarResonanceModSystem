namespace Stellar.Application.Abstractions;

/// <summary>
/// Source of a run-timer start write pushed into
/// <see cref="IDungeonStateSink.SetRunTimerStart"/>. The numeric value IS the
/// latch priority rank — LOWER is BETTER. A write wins the latch slot when the
/// slot is empty OR its rank is STRICTLY better (lower) than the currently
/// latched source's rank; equal or worse ranks are ignored. This replaces the
/// old plain first-non-zero-wins guard, which would have let the approximate
/// entry-sync source (<see cref="FlowActiveTime"/>, arrives FIRST on the live
/// path) permanently beat the precise method-55 edge that arrives after it.
/// </summary>
internal enum RunTimerSource
{
    /// <summary>
    /// Rank 1 (best) — the ARRIVAL of WorldNtf method 55
    /// (<c>NotifyStartPlayingDungeon</c>), stamped with server-now at arrival.
    /// The precise play-start edge.
    /// </summary>
    Method55Edge = 1,

    /// <summary>
    /// Rank 2 — <c>DungeonSyncData.timer_info.start_time</c>. HUD-authoritative
    /// and exact when present (live evidence: never delivered non-zero on the
    /// tapped paths — kept as a harmless exact source should it ever arrive).
    /// </summary>
    TimerInfo = 2,

    /// <summary>
    /// Rank 3 — <c>DungeonSyncData.flow_info.play_time</c>. Exact play-start
    /// epoch when present (live evidence: stays zero on the entry sync; kept as
    /// a harmless exact source).
    /// </summary>
    FlowPlayTime = 3,

    /// <summary>
    /// Rank 4 (worst) — <c>DungeonSyncData.flow_info.active_time</c>. The
    /// APPROXIMATE fallback: the entry sync stamps it with the live epoch at
    /// dungeon ENTRY, roughly one Ready-countdown EARLY. It arrives BEFORE the
    /// method-55 edge, which is exactly why the latch is rank-based.
    /// </summary>
    FlowActiveTime = 4,
}

/// <summary>
/// Outcome of a <see cref="IDungeonStateSink.SetRunTimerStart"/> write —
/// returned so the probe can emit the correct diagnostic (latch vs upgrade)
/// without a racy read-before-write check.
/// </summary>
internal enum RunTimerWrite
{
    /// <summary>Zero value, or an equal/worse-ranked source than the current latch — nothing changed.</summary>
    Ignored = 0,

    /// <summary>The slot was empty and this write latched it.</summary>
    Latched = 1,

    /// <summary>A strictly better-ranked source OVERWROTE an earlier (approximate) latch.</summary>
    Upgraded = 2,
}
