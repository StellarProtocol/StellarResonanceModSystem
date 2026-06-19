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

    // ── One-shot Z-namespace function search (diagnostic) ──────────────────────
    private bool _scanDone;
    private int _scanTickCounter;
    private int _scanAttempts;
    private const int ScanEveryTicks = 120;   // ~2s between attempts
    private const int MaxScanAttempts = 8;

    /// <summary>
    /// While diagnostics are enabled and the bridge is resolved, runs a bounded
    /// recursive scan of the <c>Z</c> Lua namespace (depth ≤3, ≤800 tables) for any
    /// function whose name contains project/plan/loadout/scheme/preset — to locate
    /// the loadout ("profession project") switch wherever it lives, now that the
    /// profession VM is confirmed to be the class system. One-shot: stops once a
    /// dump carrying the begin marker is captured (or after MaxScanAttempts).
    /// </summary>
    private void RunIntrospectionIfDue()
    {
        if (_scanDone || !StellarDiagnostics.IsEnabled || !_bridgeResolved) return;
        if (_scanTickCounter++ % ScanEveryTicks != 0) return;

        var state = GetMainLuaState();
        if (state is null || _doString is null) return;

        var giveUp = _scanAttempts++ >= MaxScanAttempts;
        try
        {
            if (!giveUp)
            {
                _doString.Invoke(state, new object[] { ScanChunk, ChunkName + ".Scan" });
            }
            var text = ReadLuaGlobalString(state, "_StellarLI");
            var complete = !string.IsNullOrEmpty(text) && text!.Contains("=== end ===", StringComparison.Ordinal);
            if (complete || (giveUp && !string.IsNullOrEmpty(text)))
            {
                foreach (var line in text!.Split('\n'))
                {
                    _log.Info(line.StartsWith("[StellarLI]", StringComparison.Ordinal) ? line : "[StellarLI] " + line);
                }
                _scanDone = true;
            }
            else if (giveUp)
            {
                _log.Info("[StellarLI] gave up: no data captured");
                _scanDone = true;
            }
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout][LI] scan threw: {inner.GetType().Name}: {inner.Message}");
        }
    }

    // Reads one Lua string global via the tolua# LuaState string indexer, decoding
    // the IL2CPP-wrapped result.
    private string? ReadLuaGlobalString(object state, string globalName)
    {
        var idx = state.GetType().GetMethod("get_Item", AnyInstance, binder: null,
            types: new[] { typeof(string) }, modifiers: null);
        if (idx is null) return null;
        var text = CoerceLuaString(idx.Invoke(state, new object[] { globalName }));
        return string.Equals(text, "Il2CppSystem.Object", StringComparison.Ordinal) ? null : text;
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

    // Progress-marker version: flush the _StellarLI global at each step so whichever
    // step the (yielding) call dies on is the last captured marker. Tries a COLON call
    // (vm:AsyncGetRolePlanData() — passes self). Awaits directly (pcall can't yield in
    // this LuaJIT runtime). C# polls until it sees the end marker.
    private const string ScanChunk =
        "local lines={} local function L(s) lines[#lines+1]=\"[StellarLI] \"..tostring(s) end" +
        " local function flush() rawset(_G,\"_StellarLI\", table.concat(lines,\"\\n\")) end" +
        " local function dump(p,t,d) if d>4 then return end for k,v in pairs(t) do local tv=type(v)" +
        "  if tv==\"table\" then L(p..tostring(k)..\":\") dump(p..\"  \",v,d+1)" +
        "  elseif tv~=\"function\" then L(p..tostring(k)..\"=\"..tostring(v)) end end end" +
        " L(\"=== begin ===\") L(\"step1 pre-coro\") flush();" +
        " (Z.CoroUtil.create_coro_xpcall(function()" +
        "  L(\"step2 in-coro\") flush()" +
        "  local vm=Z.VMMgr.GetVM(\"weapon\")" +
        "  local pid=Z.VMMgr.GetVM(\"profession\").GetCurProfession()" +
        "  L(\"step3 vm=\"..type(vm)..\" pid=\"..tostring(pid)) flush()" +
        "  local data=vm.AsyncGetRolePlanData(pid)" +
        "  L(\"step4 returned type=\"..type(data)) flush()" +
        "  if type(data)==\"table\" then dump(\"  \",data,0) else L(\"data=\"..tostring(data)) end" +
        "  L(\"=== end ===\") flush()" +
        " end))()";
}
