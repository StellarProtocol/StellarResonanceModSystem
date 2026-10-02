using System;
using System.Collections.Generic;
using System.Reflection;
namespace Stellar.Infrastructure.Hooks;

/// <summary>One callback slot per patched method in <see cref="HarmonyGameMethodHooker"/>'s tables. A second callback for
/// a method already patched is CHAINED onto the first (both run, in registration order; a throwing first never stops the
/// second) and the method is not patched again — two features on one game method (the posing despawn prefix and the
/// freeze's leave prefix on <c>ZEntityMgr.RemoveEntity</c>) used to replace each other's callback and double-patch the
/// trampoline (regression <c>hooker_second_callback_on_one_method_chains</c>). Pure (unit-tested).</summary>
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
}
