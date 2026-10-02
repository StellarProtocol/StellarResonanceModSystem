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
}
