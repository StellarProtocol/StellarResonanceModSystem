using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Hooks;

/// <summary>
/// HarmonyX adapter. Patches every overload of a named method with a shared
/// static trampoline; the trampoline dispatches to the per-method callback
/// stored in <see cref="Callbacks"/> (postfixes) or <see cref="PrefixCallbacks"/> (prefixes — a separate table so
/// one method can carry both).
/// </summary>
internal sealed class HarmonyGameMethodHooker : IGameMethodHooks
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticMembers = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Shared lookup is required because HarmonyX postfixes must be static methods.
    internal static readonly Dictionary<MethodBase, Action<object?, object?[]>> Callbacks = new();
    internal static readonly Dictionary<MethodBase, Action<object?, object?[]>> PrefixCallbacks = new();
    internal static readonly Dictionary<MethodBase, Action<object?>> ResultCallbacks = new();

    private static readonly MethodInfo TrampolineMethod =
        typeof(HarmonyGameMethodHooker).GetMethod(nameof(Trampoline), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo PrefixTrampolineMethod =
        typeof(HarmonyGameMethodHooker).GetMethod(nameof(PrefixTrampoline), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ResultTrampolineMethod =
        typeof(HarmonyGameMethodHooker).GetMethod(nameof(ResultTrampoline), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly IPluginLog _log;
    private readonly Harmony _harmony;

    public HarmonyGameMethodHooker(IPluginLog log, string harmonyId)
    {
        _log = log;
        _harmony = new Harmony(harmonyId);
    }

    public void PostfixAllOverloads(Type type, string methodName, Action<object?, object?[]> callback) =>
        PatchMatching(type, methodName, InstanceMembers, callback, prefix: false);

    /// <summary>Patches every instance overload with a PREFIX that runs <paramref name="callback"/> before the game's
    /// body (never skips it). Coexists with a postfix on the same method.</summary>
    public void PrefixAllOverloads(Type type, string methodName, Action<object?, object?[]> callback) =>
        PatchMatching(type, methodName, InstanceMembers, callback, prefix: true);

    /// <summary>Same as <see cref="PostfixAllOverloads"/> for STATIC methods (the callback's instance is null).</summary>
    public void PostfixStaticOverloads(Type type, string methodName, Action<object?, object?[]> callback) =>
        PatchMatching(type, methodName, StaticMembers, callback, prefix: false);

    /// <summary>Patches every non-void instance overload with a POSTFIX that hands <paramref name="callback"/> only the
    /// return value — no <c>__args</c> array, so a callback that early-outs costs a table lookup and allocates nothing of
    /// its own. Not zero: a reference return reaches the postfix as its Il2CppInterop managed wrapper, which the
    /// patched method's interop glue materialises whatever the callback does.</summary>
    public void PostfixResultAllOverloads(Type type, string methodName, Action<object?> callback)
    {
        foreach (var method in Matching(type, methodName, InstanceMembers).Where(m => m.ReturnType != typeof(void)))
        {
            try
            {
                ResultCallbacks[method] = callback;
                _harmony.Patch(method, postfix: new HarmonyMethod(ResultTrampolineMethod));
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name} (result postfix)");
            }
            catch (Exception ex)
            {
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    private MethodInfo[] Matching(Type type, string methodName, BindingFlags flags)
    {
        var methods = type.GetMethods(flags)
            .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition && !m.IsAbstract)
            .ToArray();
        if (methods.Length == 0) _log.Warning($"[Hooker] no method {type.FullName}.{methodName} to patch");
        return methods;
    }

    private void PatchMatching(Type type, string methodName, BindingFlags flags, Action<object?, object?[]> callback, bool prefix)
    {
        var methods = Matching(type, methodName, flags);

        foreach (var method in methods)
        {
            try
            {
                if (prefix)
                {
                    PrefixCallbacks[method] = callback;
                    _harmony.Patch(method, prefix: new HarmonyMethod(PrefixTrampolineMethod));
                }
                else
                {
                    Callbacks[method] = callback;
                    _harmony.Patch(method, postfix: new HarmonyMethod(TrampolineMethod));
                }
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name}{(prefix ? " (prefix)" : "")}");
            }
            catch (Exception ex)
            {
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    // HarmonyX postfix signature: `__instance`, `__originalMethod`, `__args` are injected by Harmony.
    private static void Trampoline(object? __instance, MethodBase __originalMethod, object[] __args) =>
        Dispatch(Callbacks, __instance, __originalMethod, __args);

    // HarmonyX prefix: void return = the original always runs. Same injected arguments as the postfix.
    private static void PrefixTrampoline(object? __instance, MethodBase __originalMethod, object[] __args) =>
        Dispatch(PrefixCallbacks, __instance, __originalMethod, __args);

    // HarmonyX postfix receiving only the return value (object: a reference return passes through unboxed).
    private static void ResultTrampoline(MethodBase __originalMethod, object? __result)
    {
        if (!ResultCallbacks.TryGetValue(__originalMethod, out var callback)) return;
        try { callback(__result); }
        catch { /* trust boundary, as Dispatch */ }
    }

    private static void Dispatch(Dictionary<MethodBase, Action<object?, object?[]>> table, object? instance,
        MethodBase original, object[] args)
    {
        if (!table.TryGetValue(original, out var callback))
        {
            return;
        }
        try
        {
            callback(instance, args);
        }
        catch
        {
            // Trust boundary: managed exceptions must not propagate back into the IL2CPP trampoline.
        }
    }
}
