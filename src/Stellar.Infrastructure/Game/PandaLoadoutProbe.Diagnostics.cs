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

    // ── Live current-loadout monitor (diagnostic) ─────────────────────────────
    private int _monitorTickCounter;
    private const int MonitorEveryTicks = 30;   // ~0.5s
    private string _lastCurDiag = string.Empty;

    /// <summary>
    /// While diagnostics are enabled and the bridge is resolved, polls the
    /// profession VM's GetCurProfession()/GetContainerProfession() every ~0.5s and
    /// logs the value whenever it CHANGES, under <c>[StellarLI][cur]</c>. Used to
    /// correlate the returned number against in-game loadout switches — i.e. to
    /// decide whether AsyncChangeProfession/GetCurProfession key on the loadout
    /// (profession project) or merely the class. The chunk is tiny (one VM call +
    /// a global write), so the per-poll cost is negligible. Silent when diagnostics
    /// are off.
    /// </summary>
    private void RunIntrospectionIfDue()
    {
        if (!StellarDiagnostics.IsEnabled || !_bridgeResolved) return;
        if (_monitorTickCounter++ % MonitorEveryTicks != 0) return;

        var state = GetMainLuaState();
        if (state is null || _doString is null) return;

        try
        {
            _doString.Invoke(state, new object[] { CurMonitorChunk, ChunkName + ".CurMon" });
            var text = ReadLuaGlobalString(state, "_StellarLI_cur");
            if (!string.IsNullOrEmpty(text) && !string.Equals(text, _lastCurDiag, StringComparison.Ordinal))
            {
                _lastCurDiag = text!;
                _log.Info("[StellarLI][cur] " + text);
            }
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout][LI] monitor threw: {inner.GetType().Name}: {inner.Message}");
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

    // Tiny per-poll monitor chunk: read the profession VM's current value + container
    // + dropId and stash a one-line summary into the _StellarLI_cur global.
    private const string CurMonitorChunk =
        "local vm=Z.VMMgr.GetVM(\"profession\")" +
        " local function c(fn) if not vm then return \"novm\" end" +
        "  local ok,r=pcall(function() return vm[fn]() end)" +
        "  if not ok then ok,r=pcall(function() return vm[fn](vm) end) end" +
        "  return ok and tostring(r) or \"ERR\" end" +
        " rawset(_G,\"_StellarLI_cur\", \"cur=\"..c(\"GetCurProfession\")..\" cont=\"..c(\"GetContainerProfession\")..\" drop=\"..c(\"GetProfessionDropId\"))";
}
