using System;
using System.Collections.Generic;
using System.Reflection;
namespace Stellar.Infrastructure.Hooks;

/// <summary>One callback slot per patched method in <see cref="HarmonyGameMethodHooker"/>'s tables. A second callback for
/// a method already patched is CHAINED onto the first (both run, in registration order; a throwing first never stops the
/// second) and the method is not patched again — two features on one game method (the posing despawn prefix and the
/// freeze's leave prefix on <c>ZEntityMgr.RemoveEntity</c>) used to replace each other's callback and double-patch the
/// trampoline (regression <c>hooker_second_callback_on_one_method_chains</c>). <see cref="Remove"/>/<see cref="RemoveResult"/>
/// undo a registration whose own Harmony patch call then threw, so a LATER registration attempt is treated as the first
/// one again instead of silently chaining onto a callback nothing actually patched (review finding). Pure (unit-tested).</summary>
internal static class HookCallbackTable
{
    /// <summary>Adds <paramref name="callback"/> for <paramref name="method"/>. True when it is the method's first one —
    /// the caller patches it; false when chained onto an existing patch.</summary>
    public static bool Add(Dictionary<MethodBase, Action<object?, object?[]>> table, MethodBase method,
        Action<object?, object?[]> callback)
    {
        if (!table.TryGetValue(method, out var first))
        {
            table[method] = callback;
            return true;
        }
        table[method] = (instance, args) =>
        {
            try { first(instance, args); }
            catch { /* the first feature's failure must not starve the second; Dispatch is the outer trust boundary */ }
            callback(instance, args);
        };
        return false;
    }

    /// <summary>Same contract as <see cref="Add"/> for a result-only (<c>Action&lt;object?&gt;</c>) callback table —
    /// <see cref="HarmonyGameMethodHooker.PostfixResultAllOverloads"/>'s own table, kept separate because its callback
    /// has no <c>__args</c>.</summary>
    public static bool AddResult(Dictionary<MethodBase, Action<object?>> table, MethodBase method, Action<object?> callback)
    {
        if (!table.TryGetValue(method, out var first))
        {
            table[method] = callback;
            return true;
        }
        table[method] = result =>
        {
            try { first(result); }
            catch { /* the first feature's failure must not starve the second */ }
            callback(result);
        };
        return false;
    }

    /// <summary>Removes <paramref name="method"/>'s entry. Called when the Harmony patch meant to back a fresh
    /// <see cref="Add"/> (return <c>true</c>) threw, so nothing actually patched the method.</summary>
    public static void Remove(Dictionary<MethodBase, Action<object?, object?[]>> table, MethodBase method) => table.Remove(method);

    /// <summary>Same as <see cref="Remove"/> for <see cref="AddResult"/>'s table.</summary>
    public static void RemoveResult(Dictionary<MethodBase, Action<object?>> table, MethodBase method) => table.Remove(method);

    /// <summary>True when <paramref name="method"/> already carries the shared PREFIX trampoline — through a callback OR a
    /// gate (both tables share one patch; patching again would run every callback twice).</summary>
    public static bool PrefixPatched(Dictionary<MethodBase, Action<object?, object?[]>> callbacks,
        Dictionary<MethodBase, Func<object?, object?[], bool>> gates, MethodBase method) =>
        callbacks.ContainsKey(method) || gates.ContainsKey(method);

    /// <summary>Adds a run-original GATE for <paramref name="method"/>; a second gate is AND-ed onto the first (either may
    /// veto). True when it is the method's first gate.</summary>
    public static bool AddGate(Dictionary<MethodBase, Func<object?, object?[], bool>> gates, MethodBase method,
        Func<object?, object?[], bool> gate)
    {
        if (!gates.TryGetValue(method, out var first))
        {
            gates[method] = gate;
            return true;
        }
        gates[method] = (instance, args) => Ask(first, instance, args) & Ask(gate, instance, args);
        return false;
    }

    /// <summary>The shared prefix trampoline's whole decision (combat-freeze deferred removal, owner 2026-10-02): the
    /// method's gate is asked FIRST; a veto skips the game's body AND every chained prefix callback, so a deferred call
    /// runs none of them (posing's despawn, the freeze's leave) — they run exactly once, when the call is replayed and the
    /// gate lets it through. No gate, or a gate that throws (fail open): the callbacks run and so does the original.
    /// Returns HarmonyX's run-original flag.</summary>
    public static bool RunPrefix(Dictionary<MethodBase, Func<object?, object?[], bool>> gates,
        Dictionary<MethodBase, Action<object?, object?[]>> callbacks, MethodBase method, object? instance, object?[] args)
    {
        if (gates.TryGetValue(method, out var gate) && !Ask(gate, instance, args)) return false;
        if (callbacks.TryGetValue(method, out var callback))
        {
            try { callback(instance, args); }
            catch { /* trust boundary: a managed exception must not propagate back into the IL2CPP trampoline */ }
        }
        return true;
    }

    private static bool Ask(Func<object?, object?[], bool> gate, object? instance, object?[] args)
    {
        try { return gate(instance, args); }
        catch { return true; }   // a broken gate never blocks the game
    }
}
