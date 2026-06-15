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
/// stored in <see cref="Callbacks"/>.
/// </summary>
internal sealed class HarmonyGameMethodHooker
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Shared lookup is required because HarmonyX postfixes must be static methods.
    internal static readonly Dictionary<MethodBase, Action<object?, object?[]>> Callbacks = new();

    private static readonly MethodInfo TrampolineMethod =
        typeof(HarmonyGameMethodHooker).GetMethod(nameof(Trampoline), BindingFlags.Static | BindingFlags.NonPublic)!;

    private readonly IPluginLog _log;
    private readonly Harmony _harmony;

    public HarmonyGameMethodHooker(IPluginLog log, string harmonyId)
    {
        _log = log;
        _harmony = new Harmony(harmonyId);
    }

    public void PostfixAllOverloads(Type type, string methodName, Action<object?, object?[]> callback)
    {
        var methods = type.GetMethods(InstanceMembers)
            .Where(m => m.Name == methodName && !m.IsGenericMethodDefinition)
            .ToArray();

        if (methods.Length == 0)
        {
            _log.Warning($"[Hooker] no method {type.FullName}.{methodName} to patch");
            return;
        }

        foreach (var method in methods)
        {
            try
            {
                Callbacks[method] = callback;
                _harmony.Patch(method, postfix: new HarmonyMethod(TrampolineMethod));
                _log.Info($"[Hooker] patched {type.FullName}.{method.Name}");
            }
            catch (Exception ex)
            {
                _log.Error($"[Hooker] failed to patch {type.FullName}.{method.Name}: {ex.Message}");
            }
        }
    }

    // HarmonyX postfix signature: `__instance`, `__originalMethod`, `__args` are injected by Harmony.
    private static void Trampoline(object? __instance, MethodBase __originalMethod, object[] __args)
    {
        if (!Callbacks.TryGetValue(__originalMethod, out var callback))
        {
            return;
        }
        try
        {
            callback(__instance, __args);
        }
        catch
        {
            // Trust boundary: managed exceptions must not propagate back into the IL2CPP trampoline.
        }
    }
}
