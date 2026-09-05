namespace Stellar.Infrastructure.Game;

/// <summary>
/// Logout session-reset for <see cref="PandaLoadoutProbe"/>. Split into its own partial (rather
/// than living in <c>PandaLoadoutProbe.cs</c>) to stay under the file-size standards gate.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    /// <summary>Reset character-scoped session state on logout: the parsed Deep-Slumber state, the
    /// equipped-imagine latch, the LIVE-line class/talents, and the raw-dump memos (so the next
    /// login's first parse is not skipped as "unchanged"). Re-arms the on-demand refresh AND the
    /// container-merge flag, so the first in-world drain tick after the next login re-reads the new
    /// character immediately rather than waiting for that character's first delta. Also drops any
    /// un-consumed change flag — the PREVIOUS character's change must never surface as this one's.
    /// Does NOT reset bridge resolution (process-scoped, not character-scoped).</summary>
    internal void ClearSession()
    {
        _deepSlumberState = null;
        _resonanceInstalled = null;
        _liveProfessionId = 0;
        _liveTalentStageId = 0;
        _liveTalentNodes = null;
        _lastDataRaw = null;
        _lastLiveStateRaw = null;
        _liveStateChanged = false;
        _liveStatePendingPublish = false;   // …including one still waiting on the per-class resolve
        _mergePending = true;
        _refreshPending = true;
        // Switch-gate state is per-character too: the next character's worn plan is unknown until its
        // first parse (never "still plan N"), and its 3 s switch cooldown is its own.
        _liveCurrentPlanId = UnknownPlanId;
        System.Threading.Interlocked.Exchange(ref _lastSwitchDispatchMs, 0);
        while (_toTip.TryDequeue(out _)) { }   // a refusal tip owed to the PREVIOUS character never fires
    }
}
