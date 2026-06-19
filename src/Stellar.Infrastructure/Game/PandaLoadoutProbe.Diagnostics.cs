using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for <see cref="PandaLoadoutProbe"/>. Per-event dispatch
/// / result lines are gated on <see cref="StellarDiagnostics.IsEnabled"/>; the
/// one-shot bridge-resolution line fires unconditionally so the scenario gates have
/// evidence the Lua bridge resolved even on a non-diagnostic run.
///
/// <para>The marquee piece here is the one-shot in-world <b>introspection</b>
/// (<see cref="RunIntrospectionIfDue"/>): once the Lua bridge resolves and we are
/// in-world, it runs a Lua chunk that enumerates the candidate profession/loadout VMs
/// and their members + dumps the current-id container, logging everything under the
/// greppable <c>[StellarLI]</c> prefix so the VM key, apply-function name + arity, and
/// saved-project list getter can be pinned from the BepInEx log. All gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private int _failedResolutionAttempts;
    private const int ResolutionFailureLogEvery = 60;

    // Always-on one-shot: proves the Lua-bridge reflection targets resolved.
    private void OnResolutionSucceeded()
    {
        _log.Info(
            "[Stellar][Loadout] resolved switch bridge: tolua# LuaState.mainState + DoString" +
            (_applyFnResolved
                ? $"; apply Z.VMMgr.GetVM(\"{ProfessionVmName}\").{ApplyFnName}"
                : "; apply fn NOT yet pinned (run with STELLAR_DIAGNOSTICS=1 to introspect)"));
    }

    private void OnResolutionFailure(string reason)
    {
        _failedResolutionAttempts++;
        if (!_resolutionFailureLogged)
        {
            _resolutionFailureLogged = true;
            _log.Warning($"[Stellar][Loadout] bridge not resolved: {reason}");
            return;
        }
        if (_failedResolutionAttempts % ResolutionFailureLogEvery == 0)
        {
            _log.Warning($"[Stellar][Loadout] bridge still not resolved ({_failedResolutionAttempts} attempts): {reason}");
        }
    }

    private void DiagDispatched(int projectId)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] ChangeProfessionProject(id={projectId}) dispatched");
    }

    private void DiagResult(int projectId, LoadoutResult result, long elapsedMs)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Loadout] switch(id={projectId}) result: {result} after {elapsedMs}ms");
    }

    // ── In-world introspection (retried until cap) ─────────────────────────────
    private bool _introspectionDone;
    private int _introspectionTickCounter;
    private int _introspectionAttempts;
    private const int IntrospectionEveryTicks = 120;   // ~2s between dumps
    private const int MaxIntrospectionAttempts = 12;   // ~24s window to open the panel

    /// <summary>
    /// While diagnostics are enabled and the bridge is resolved, runs the Lua
    /// introspection chunk every ~2s (up to <see cref="MaxIntrospectionAttempts"/>)
    /// to discover the loadout/profession VM + its members — repeated so whichever
    /// fire lands after the player opens the Adventurer/Loadout panel captures it.
    /// NOT gated on the current-id read (that path is unreliable until the VM is
    /// pinned, and its tick counter freezes once the bridge resolves). Silent no-op
    /// when diagnostics are off.
    /// </summary>
    private void RunIntrospectionIfDue()
    {
        if (_introspectionDone || !StellarDiagnostics.IsEnabled || !_bridgeResolved) return;
        if (_introspectionTickCounter++ % IntrospectionEveryTicks != 0) return;
        if (_introspectionAttempts++ >= MaxIntrospectionAttempts)
        {
            _introspectionDone = true;
            return;
        }

        var state = GetMainLuaState();
        if (state is null || _doString is null)
        {
            _log.Info("[Stellar][Loadout][LI] introspection skipped: main Lua state unavailable");
            return;
        }

        try
        {
            _doString.Invoke(state, new object[] { IntrospectionChunk, ChunkName + ".Introspect" });
            _log.Info("[Stellar][Loadout][LI] introspection chunk dispatched — grep BepInEx log for [StellarLI]");
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout][LI] introspection threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    // Lua chunk: enumerate candidate VM keys, dump each resolved VM's members, then
    // for the strongest candidates probe known-shape getters (project list) and log
    // the entry fields. Logs to the BepInEx log via CS.UnityEngine.Debug.Log under
    // [StellarLI]. Wrapped in create_coro_xpcall so any error is logged by the game's
    // own handler rather than crashing the chunk. No external text interpolated.
    private const string IntrospectionChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local L=function(s) CS.UnityEngine.Debug.Log(\"[StellarLI] \"..tostring(s)) end" +
        " L(\"=== loadout VM introspection begin ===\")" +
        " local keys={\"profession\",\"professionproject\",\"profession_project\",\"equip\"," +
        "\"loadout\",\"role\",\"adventurer\",\"build\",\"professionview\",\"professionmain\",\"spec\"}" +
        " for _,n in ipairs(keys) do" +
        "  local ok,vm=pcall(function() return Z.VMMgr.GetVM(n) end)" +
        "  if ok and vm then" +
        "   L(\"VM '\"..n..\"' resolved; members:\")" +
        "   for k,v in pairs(vm) do" +
        "    if type(v)==\"function\" then" +
        "     local lk=tostring(k):lower()" +
        "     if lk:find(\"project\") or lk:find(\"profession\") or lk:find(\"change\")" +
        " or lk:find(\"switch\") or lk:find(\"use\") or lk:find(\"apply\") or lk:find(\"list\")" +
        " or lk:find(\"get\") or lk:find(\"current\") then" +
        "      L(\"  fn \"..tostring(k))" +
        "     end" +
        "    else" +
        "     L(\"  \"..tostring(k)..\"=\"..type(v))" +
        "    end" +
        "   end" +
        "  end" +
        " end" +
        " L(\"=== VMMgr registry keys (fallback) ===\")" +
        " local mok,mgr=pcall(function() return Z.VMMgr end)" +
        " if mok and mgr then for k,v in pairs(mgr) do L(\"  reg \"..tostring(k)..\"=\"..type(v)) end end" +
        " L(\"=== loadout VM introspection end ===\")" +
        " end))()";
}
