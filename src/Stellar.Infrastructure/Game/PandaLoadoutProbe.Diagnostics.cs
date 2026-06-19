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
        if (_scanAttempts++ >= MaxScanAttempts) { _scanDone = true; return; }

        var state = GetMainLuaState();
        if (state is null || _doString is null) return;

        try
        {
            _doString.Invoke(state, new object[] { ScanChunk, ChunkName + ".Scan" });
            var text = ReadLuaGlobalString(state, "_StellarLI");
            if (!string.IsNullOrEmpty(text) && text!.Contains("=== begin ===", StringComparison.Ordinal))
            {
                foreach (var line in text.Split('\n'))
                {
                    _log.Info(line.StartsWith("[StellarLI]", StringComparison.Ordinal) ? line : "[StellarLI] " + line);
                }
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

    // VMs live in Z.VMMgr.GetVM's closure (an upvalue table), NOT as reachable Z
    // subtables — so Z-traversal can't see them. Use debug.getupvalue to read the
    // VM-registry/cache table off GetVM/Init and enumerate EVERY registered VM key.
    private const string ScanChunk =
        "local lines={} local function L(s) lines[#lines+1]=\"[StellarLI] \"..tostring(s) end" +
        " L(\"=== begin ===\")" +
        " if not debug or not debug.getupvalue then L(\"no debug lib\") else" +
        "  local function dumpups(label, fn)" +
        "   if type(fn)~=\"function\" then L(label..\" not a function\") return end" +
        "   local i=1 while true do local n,v=debug.getupvalue(fn,i) if not n then break end" +
        "    L(label..\" up[\"..i..\"] \"..tostring(n)..\"=\"..type(v))" +
        "    if type(v)==\"table\" then local cnt=0" +
        "     for k,vv in pairs(v) do cnt=cnt+1 if cnt<=120 then L(\"    key \"..tostring(k)..\"=\"..type(vv)) end end" +
        "     L(\"    (#keys=\"..cnt..\")\") end" +
        "    i=i+1 end end" +
        "  dumpups(\"GetVM\", Z.VMMgr.GetVM)" +
        "  dumpups(\"Init\", Z.VMMgr.Init)" +
        " end" +
        " L(\"=== end ===\")" +
        " rawset(_G,\"_StellarLI\", table.concat(lines,\"\\n\"))";
}
