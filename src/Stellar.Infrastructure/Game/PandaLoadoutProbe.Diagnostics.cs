using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
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
            // Primary channel: read the dump the chunk stashed in the _StellarLI global
            // (independent of whether UnityEngine.Debug.Log routes to the BepInEx log).
            if (DumpLuaGlobalString(state, "_StellarLI"))
            {
                _introspectionDone = true;   // captured — stop repeating
            }
            else
            {
                _log.Info("[Stellar][Loadout][LI] chunk dispatched but _StellarLI global was nil/empty this pass");
            }
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout][LI] introspection threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    // Reads a Lua string global (the introspection dump) via the tolua# LuaState
    // string indexer (object this[string]) and logs each line. Returns true if a
    // non-empty value was read.
    private bool DumpLuaGlobalString(object state, string globalName)
    {
        try
        {
            var idx = state.GetType().GetMethod("get_Item", AnyInstance, binder: null,
                types: new[] { typeof(string) }, modifiers: null);
            if (idx is null)
            {
                _log.Warning("[Stellar][Loadout][LI] LuaState string indexer not found — cannot read dump global");
                return false;
            }
            var val = idx.Invoke(state, new object[] { globalName });
            var text = CoerceLuaString(val);
            if (string.IsNullOrEmpty(text) || string.Equals(text, "Il2CppSystem.Object", StringComparison.Ordinal))
            {
                return false;
            }
            foreach (var line in text.Split('\n'))
            {
                _log.Info(line.StartsWith("[StellarLI]", StringComparison.Ordinal) ? line : "[StellarLI] " + line);
            }
            // Only a dump carrying the begin marker counts as a real capture (so a
            // garbled/partial read doesn't prematurely stop the retries).
            return text.Contains("=== begin ===", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _log.Warning($"[Stellar][Loadout][LI] global read failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // The tolua# LuaState string indexer returns the Lua string boxed as an
    // Il2CppSystem.Object whose managed ToString() yields the wrapper type name, not
    // the content. Decode the underlying IL2CPP string via the interop runtime.
    private static string? CoerceLuaString(object? val)
    {
        if (val is null) return null;
        if (val is string s) return s;
        if (val is Il2CppObjectBase ob)
        {
            try
            {
                var ptr = ob.Pointer;
                if (ptr != IntPtr.Zero) return IL2CPP.Il2CppStringToManaged(ptr);
            }
            catch { /* not an IL2CPP string — fall through */ }
        }
        return val.ToString();
    }

    // Lua chunk: enumerate candidate VM keys, dump each resolved VM's members, then
    // for the strongest candidates probe known-shape getters (project list) and log
    // the entry fields. Logs to the BepInEx log via CS.UnityEngine.Debug.Log under
    // [StellarLI]. Wrapped in create_coro_xpcall so any error is logged by the game's
    // own handler rather than crashing the chunk. No external text interpolated.
    private const string IntrospectionChunk =
        "local lines={}" +
        " local function L(s) lines[#lines+1]=\"[StellarLI] \"..tostring(s) end" +
        " local function dumpfns(p,t) if type(t)~=\"table\" then return end local n=0" +
        "  for k,v in pairs(t) do if type(v)==\"function\" then n=n+1 if n<=90 then L(p..tostring(k)) end end end" +
        "  L(p..\"(#fns=\"..n..\")\") end" +
        " L(\"=== begin ===\")" +
        " for _,n in ipairs({\"profession\",\"equip\"}) do" +
        "  local ok,vm=pcall(function() return Z.VMMgr.GetVM(n) end)" +
        "  if ok and vm then L(\"VM '\"..n..\"' direct:\") dumpfns(\"  \", vm)" +
        "   local mt=getmetatable(vm)" +
        "   if mt and type(mt.__index)==\"table\" then L(\"VM '\"..n..\"' __index:\") dumpfns(\"  mt:\", mt.__index) end" +
        "  end end" +
        " local keys={\"professionproject\",\"equipplan\",\"plan\",\"preset\",\"scheme\",\"talent\"," +
        "\"attr\",\"attribute\",\"adventurer\",\"character\",\"role\",\"build\",\"loadout\",\"gearplan\"," +
        "\"gear\",\"equipgroup\",\"equipscheme\",\"professionplan\",\"professionscheme\",\"professionconfig\"," +
        "\"equipsave\",\"specplan\",\"spec\",\"attrplan\",\"talentplan\",\"planmain\",\"professionprojectmain\"}" +
        " for _,n in ipairs(keys) do" +
        "  local ok,vm=pcall(function() return Z.VMMgr.GetVM(n) end)" +
        "  if ok and vm and type(vm)==\"table\" then L(\"FOUND VM '\"..n..\"':\") dumpfns(\"  \", vm) end end" +
        " local pvm=Z.VMMgr.GetVM(\"profession\")" +
        " if pvm then for _,fn in ipairs({\"GetCurProfessionProjectId\",\"GetProfessionProjectId\"," +
        "\"GetCurProject\",\"GetProjectId\",\"GetCurLoadout\",\"GetProfessionProjectList\"," +
        "\"GetProfessionProjectInfoList\",\"GetAllProfessionProject\",\"GetProjectInfoList\"}) do" +
        "  local ok,r=pcall(function() return pvm[fn]() end)" +
        "  if not ok then ok,r=pcall(function() return pvm[fn](pvm) end) end" +
        "  if ok then L(\"call \"..fn..\" -> \"..type(r)..\" \"..tostring(r)) end end end" +
        " L(\"=== end ===\")" +
        " rawset(_G,\"_StellarLI\", table.concat(lines,\"\\n\"))";
}
