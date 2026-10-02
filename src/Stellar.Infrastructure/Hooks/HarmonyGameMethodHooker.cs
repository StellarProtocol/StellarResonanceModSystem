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
    internal static readonly Dictionary<MethodBase, Func<object?, object?[], bool>> PrefixGates = new();

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

    /// <summary>Patches every instance overload with a run-original GATE on the shared prefix trampoline (one patch with
    /// <see cref="PrefixAllOverloads"/>'s callbacks): when <paramref name="runOriginal"/> returns false the game's body AND
    /// the method's chained prefix callbacks are skipped (<see cref="HookCallbackTable.RunPrefix"/>).</summary>
    public void GatePrefixAllOverloads(Type type, string methodName, Func<object?, object?[], bool> runOriginal)
    {
        foreach (var method in Matching(type, methodName, InstanceMembers))
        {
            if (Refused(method)) continue;
            var patched = HookCallbackTable.PrefixPatched(PrefixCallbacks, PrefixGates, method);
            HookCallbackTable.AddGate(PrefixGates, method, runOriginal);
            if (patched) { _log.Info($"[Hooker] chained a gate on {type.FullName}.{method.Name} (prefix)"); continue; }
            try
            {
                _harmony.Patch(method, prefix: new HarmonyMethod(PrefixTrampolineMethod));
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name} (gated prefix)");
            }
            catch (Exception ex)
            {
                PrefixGates.Remove(method);
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

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
            if (Refused(method)) continue;
            try
            {
                if (!HookCallbackTable.AddResult(ResultCallbacks, method, callback))
                {
                    _log.Info($"[Hooker] chained a result callback on {type.FullName}.{method.Name}");
                    continue;   // already patched: one trampoline, both callbacks
                }
                _harmony.Patch(method, postfix: new HarmonyMethod(ResultTrampolineMethod));
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name} (result postfix)");
            }
            catch (Exception ex)
            {
                HookCallbackTable.RemoveResult(ResultCallbacks, method);
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    /// <summary>Patches one method with a caller-supplied static PREFIX — for a hot game method whose prefix must not pay
    /// the shared trampoline's per-call <c>__args</c> array (it can take its arguments by position, e.g.
    /// <c>ref float __0</c>). False when the patch failed (logged).</summary>
    public bool PrefixWith(MethodBase method, MethodInfo prefix)
    {
        if (Refused(method)) return false;
        try
        {
            _harmony.Patch(method, prefix: new HarmonyMethod(prefix));
            _log.Info($"[Hooker] patched {method.DeclaringType?.FullName}.{method.Name} (prefix {prefix.Name})");
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"[Hooker] failed to patch {method.DeclaringType?.FullName}.{method.Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True (logged as an error) when <paramref name="method"/>'s signature — a parameter or the return type — holds a
    /// type the Il2CppInterop trampoline mis-marshals (<see cref="Il2CppPatchSafety"/>; freeze-crash root cause 2026-10-02,
    /// by-ref corruption measured on CoreCLR 6.0.7): patching it would corrupt every call the game makes to it, so it is
    /// never patched.</summary>
    private bool Refused(MethodBase method)
    {
        var types = method.GetParameters().Select(p => p.ParameterType).ToArray();
        var returns = method is MethodInfo info ? info.ReturnType : typeof(void);
        if (Il2CppPatchSafety.FirstHazard(types, returns, Il2CppStructSizes.Of) is not { } hazard) return false;
        _log.Error($"[Hooker] refused to patch {method.DeclaringType?.FullName}.{method.Name}: {Il2CppPatchSafety.Describe(hazard, types, returns)} " +
                   "that Il2CppInterop mis-marshals (corrupt arguments / native crash) — needs a native detour");
        return true;
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
            if (Refused(method)) continue;
            try
            {
                // A prefix shares its trampoline with a gate (GatePrefixAllOverloads): already patched = chain only.
                var gated = prefix && HookCallbackTable.PrefixPatched(PrefixCallbacks, PrefixGates, method);
                if (!HookCallbackTable.Add(prefix ? PrefixCallbacks : Callbacks, method, callback) || gated)
                {
                    _log.Info($"[Hooker] chained a callback on {type.FullName}.{method.Name}{(prefix ? " (prefix)" : "")}");
                    continue;   // already patched: one trampoline, both callbacks
                }
                if (prefix) _harmony.Patch(method, prefix: new HarmonyMethod(PrefixTrampolineMethod));
                else _harmony.Patch(method, postfix: new HarmonyMethod(TrampolineMethod));
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name}{(prefix ? " (prefix)" : "")}");
            }
            catch (Exception ex)
            {
                // The Add above already reserved the slot; undo it so a later registration attempt for the same method
                // is treated as the first one again instead of silently chaining onto a callback nothing patched
                // (review finding) — only reachable here when Add returned true, so this is a safe no-op otherwise.
                HookCallbackTable.Remove(prefix ? PrefixCallbacks : Callbacks, method);
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    // HarmonyX postfix signature: `__instance`, `__originalMethod`, `__args` are injected by Harmony.
    private static void Trampoline(object? __instance, MethodBase __originalMethod, object[] __args) =>
        Dispatch(Callbacks, __instance, __originalMethod, __args);

    // HarmonyX prefix: true = the original runs (always, unless the method's gate vetoes — then its callbacks are
    // skipped too). Same injected arguments as the postfix.
    private static bool PrefixTrampoline(object? __instance, MethodBase __originalMethod, object[] __args) =>
        HookCallbackTable.RunPrefix(PrefixGates, PrefixCallbacks, __originalMethod, __instance, __args);

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
