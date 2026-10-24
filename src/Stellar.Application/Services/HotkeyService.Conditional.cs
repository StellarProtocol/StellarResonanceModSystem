using System;

namespace Stellar.Application.Services;

// HotkeyAction.IsActive — "only while" gated actions (2.26.0). A gated action is dormant while its gate is false: Tick does
// not dispatch it AND its chord is left out of the game-side block set, so the press reaches the game untouched even with
// "Don't pass hotkeys through to the game" on. Ungated actions (IsActive null — every action declared before this) are
// unaffected: IsLive is true and SyncBlockedKeys includes them exactly as before.
//
// Why poll every tick instead of evaluating the gate at block time: the block is a Rewired.Keyboard prefix the game hits
// ~8–25k times/sec (HotkeyKeyBlockPatch perf contract) — calling plugin delegates there is out of the question. So the gates
// are latched here once per tick and the static block set is rebuilt only on a FLIP (window opened / closed), which is
// also when the prefixes may (un)install. One frame of lag on a flip is harmless: a press needs a separate frame anyway.
internal sealed partial class HotkeyService
{
    // Set once any gated action is declared, never cleared (a cheap skip for the common no-gate case, not bookkeeping).
    private bool _anyConditional;

    // Dispatch-time check. Evaluated live (not the latch), so it is right even while block-all is off and no refresh runs.
    private bool IsLive(RegisteredAction action) => action.IsActive is null || EvalGate(action);

    private void RefreshConditionalBlocks()
    {
        // Only the block set depends on the latch; with block-all off nothing is held back, so there is nothing to refresh.
        if (!_anyConditional || !_blockAllFromGame) return;
        bool flipped = false;
        foreach (var a in _actions.Values)
        {
            if (a.IsActive is null || a.CurrentBinding is null) continue;
            bool now = EvalGate(a);
            if (now == a.ActiveLatched) continue;
            a.ActiveLatched = now;
            flipped = true;
        }
        if (flipped) SyncBlockedKeys();
    }

    // A throwing gate counts as inactive (the key then passes through to the game), logged at most every 30 s per action.
    private bool EvalGate(RegisteredAction action)
    {
        try { return action.IsActive!(); }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            if (!_lastErrorLog.TryGetValue(action.Id, out var last) || now - last >= ErrorLogInterval)
            {
                _lastErrorLog[action.Id] = now;
                _log.Error($"[Hotkeys] action '{action.Id}' IsActive gate threw: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }
}
