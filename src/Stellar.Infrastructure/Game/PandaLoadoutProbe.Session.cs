namespace Stellar.Infrastructure.Game;

/// <summary>
/// Logout session-reset for <see cref="PandaLoadoutProbe"/>. Split into its own partial (rather
/// than living in <c>PandaLoadoutProbe.cs</c>) to stay under the file-size standards gate.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    /// <summary>Reset character-scoped session state on logout: the parsed Deep-Slumber state, the
    /// equipped-imagine latch, the LIVE-line class/talents, and the raw-dump memo (so the next
    /// login's first parse is not skipped as "unchanged"). Re-arms the on-demand refresh so the next
    /// in-world tick re-reads the new character promptly. Does NOT reset bridge resolution
    /// (process-scoped, not character-scoped).</summary>
    internal void ClearSession()
    {
        _deepSlumberState = null;
        _resonanceInstalled = null;
        _liveProfessionId = 0;
        _liveTalentStageId = 0;
        _liveTalentNodes = null;
        _lastDataRaw = null;
        _refreshPending = true;
    }
}
