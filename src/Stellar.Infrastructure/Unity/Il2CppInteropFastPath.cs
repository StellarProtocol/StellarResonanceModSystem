using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Native early-outs in front of two of Il2CppInterop's injector hooks.
/// <para>
/// Those hooks (installed by <c>InjectorHelpers.Setup</c> at the first type injection, process-global, never removed)
/// detour native il2cpp functions to MANAGED delegates. The game calls both constantly — <c>Class::FromIl2CppType</c>
/// from variant-generic <c>IsAssignableFrom</c> (<c>is</c>/<c>as</c>, per entity per frame), and
/// <c>MetadataCache::GetTypeInfoFromTypeDefinitionIndex</c> from INSIDE that original — and every call paid a
/// reverse-P/Invoke + forward-P/Invoke round trip (plus a ConcurrentDictionary lookup in the second) even though only
/// fake injected-type tokens (always negative) need the managed code. The first alone measured ~10–12% of the raid
/// main thread (~4–5 ms/frame); the nested one ~2–4%.
/// </para>
/// <para>
/// Fix: per hook, a tiny native stub (<see cref="InteropFastPathCode"/>) that tests the sign of the token and jumps
/// either to the ORIGINAL function's trampoline (non-negative: what the managed hook would have done anyway) or to the
/// managed hook's thunk (negative: unchanged path). Installed by swapping Dobby's detour pointer slot from the managed
/// thunk to the stub with one atomic compare-and-swap. Every address is cross-checked first; any mismatch or exception
/// skips THAT stub with one log line and leaves its original detour in place — the stubs install independently. Same
/// family as <see cref="Il2CppClassInitFix"/> (reflective repair of Il2CppInterop's injector internals).
/// </para>
/// Kill switch (both stubs): <c>STELLAR_NO_INTEROP_FASTPATH=1</c> or a <c>NO_INTEROP_FASTPATH</c> line in <c>stellar_perf.flags</c>.
/// </summary>
internal static class Il2CppInteropFastPath
{
    /// <summary>Launch flag (<see cref="PerfControls.Flag"/>) that disables the fast path.</summary>
    public const string KillSwitchFlag = "NO_INTEROP_FASTPATH";

    private const string Tag = "[InteropFastPath]";
    private const string HooksNs = "Il2CppInterop.Runtime.Injection.Hooks.";

    /// <summary>
    /// One hooked function: where Il2CppInterop keeps its hook object (<c>InjectorHelpers.&lt;Field&gt;</c>), the exact
    /// hook type whose managed <c>Hook</c> body the stub mirrors, a shape check of that body, and the stub builder.
    /// </summary>
    private sealed record HookSpec(
        string Field, string TypeName, string Label,
        Func<Type, Delegate, string?> CheckShape, Func<long, long, byte[]> BuildStub);

    private static readonly HookSpec[] Specs =
    {
        new("FromIl2CppTypeHook", HooksNs + "Class_FromIl2CppType_Hook", "Class::FromIl2CppType",
            CheckFromIl2CppTypeShape, InteropFastPathCode.BuildStub),
        new("GetTypeInfoFromTypeDefinitionIndexHook", HooksNs + "MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook",
            "MetadataCache::GetTypeInfoFromTypeDefinitionIndex", CheckTypeDefIndexShape, InteropFastPathCode.BuildIndexStub),
    };

    private enum State { NotTried, Pending, Done }
    private static State _state;
    private static readonly bool[] Finished = new bool[Specs.Length];

    /// <summary>
    /// Install once (main thread, framework startup, after the first type injection). If Il2CppInterop has not
    /// applied a hook yet, stays pending and <see cref="RetryIfPending"/> tries exactly once more for the stubs still
    /// missing. Never throws.
    /// </summary>
    public static void TryInstall(IPluginLog log)
    {
        if (_state == State.Done) return;
        bool lastChance = _state == State.Pending;
        _state = State.Done;
        if (PerfControls.Flag(KillSwitchFlag))
        {
            log.Info($"{Tag} skipped (all stubs): disabled by the {KillSwitchFlag} launch flag");
            return;
        }
        for (int i = 0; i < Specs.Length; i++)
        {
            if (Finished[i]) continue;
            bool notYet = TryInstallOne(Specs[i], log, lastChance);
            if (notYet) _state = State.Pending;
            else Finished[i] = true;
        }
    }

    /// <summary>Second (final) attempt when the first found a hook not applied yet. No-op otherwise.</summary>
    public static void RetryIfPending(IPluginLog log)
    {
        if (_state == State.Pending) TryInstall(log);
    }

    /// <summary>Returns true when the hook is not applied yet and a retry is still allowed (nothing logged above debug).</summary>
    private static bool TryInstallOne(HookSpec spec, IPluginLog log, bool lastChance)
    {
        string name = spec.Field;
        try
        {
            string result = Install(spec, out bool notYet);
            if (notYet && !lastChance)
            {
                log.Debug($"{Tag} {name}: hook not applied yet — will retry once on the first ticker frame");
                return true;
            }
            if (result.StartsWith("installed", StringComparison.Ordinal)) log.Info($"{Tag} {name} {result}");
            else log.Warning($"{Tag} {name} {result}");
        }
        catch (Exception ex)
        {
            log.Warning($"{Tag} {name} skipped: {ex.GetType().Name}: {ex.Message}");
        }
        return false;
    }

    private static string Install(HookSpec spec, out bool notYet)
    {
        notYet = false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "skipped: not Windows x64";
        string? fail = ReadHook(spec, out HookParts parts, out notYet);
        if (fail != null) return fail;
        fail = ResolveAddresses(parts, out Addresses a);
        if (fail != null) return fail;
        return VerifyAndPatch(spec, a);
    }

    // ── 1. Il2CppInterop internals (reflection; internal API — every shape assumption is checked) ─────────────────
    private readonly record struct HookParts(object Hook, Delegate Detour, Delegate Original, Delegate Method);
    private readonly record struct Addresses(IntPtr Target, IntPtr Tramp, IntPtr Thunk);

    private static Type? InjectorHelpersType()
        => typeof(ClassInjector).Assembly.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers");

    private static string? ReadHook(HookSpec spec, out HookParts parts, out bool notYet)
    {
        parts = default;
        notYet = false;
        object? hook = InjectorHelpersType()?.GetField(spec.Field, BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (hook == null) return $"skipped: InjectorHelpers.{spec.Field} not found (Il2CppInterop changed?)";
        Type hookType = hook.GetType();
        if (hookType.FullName != spec.TypeName) return $"skipped: unexpected hook type {hookType.FullName}";

        Type? hookBase = hookType.BaseType;
        while (hookBase != null && !(hookBase.IsGenericType && hookBase.GetGenericTypeDefinition().Name == "Hook`1"))
            hookBase = hookBase.BaseType;
        if (hookBase == null) return "skipped: Hook<T> base class not found";
        const BindingFlags inst = BindingFlags.NonPublic | BindingFlags.Instance;
        FieldInfo? fApplied = hookBase.GetField("_isApplied", inst);
        FieldInfo? fDetour = hookBase.GetField("_detour", inst);
        FieldInfo? fOriginal = hookBase.GetField("_original", inst);
        FieldInfo? fMethod = hookBase.GetField("_method", inst);
        if (fApplied == null || fDetour == null || fOriginal == null || fMethod == null)
            return "skipped: Hook<T> fields (_isApplied/_detour/_original/_method) not found";
        if (!(bool)fApplied.GetValue(hook)!) { notYet = true; return $"skipped: {spec.Label} hook was never applied"; }

        var detour = fDetour.GetValue(hook) as Delegate;
        var original = fOriginal.GetValue(hook) as Delegate;
        var method = fMethod.GetValue(hook) as Delegate;
        if (detour == null || original == null || method == null) return "skipped: hook delegates are null";

        // The managed body must be the one whose semantics the stub mirrors (negative token → lookup, else Original).
        if (detour.Method.Name != "Hook" || detour.Method.DeclaringType != hookType)
            return $"skipped: unexpected detour method {detour.Method.DeclaringType?.Name}.{detour.Method.Name}";
        string? shape = spec.CheckShape(hookType, detour);
        if (shape != null) return $"skipped: {shape}";
        parts = new HookParts(hook, detour, original, method);
        return null;
    }

    /// <summary><c>Hook(Il2CppType* type, bool throwOnError)</c>; the stub reads <c>type-&gt;data</c> at offset 0.</summary>
    private static string? CheckFromIl2CppTypeShape(Type hookType, Delegate detour)
    {
        if (detour.Method.GetParameters().Length != 2) return "detour is not Hook(Il2CppType*, bool)";
        Type? typeStruct = hookType.GetNestedType("Il2CppType", BindingFlags.NonPublic | BindingFlags.Public);
        if (typeStruct == null || Marshal.OffsetOf(typeStruct, "data") != IntPtr.Zero)
            return "Il2CppType.data is not at offset 0";
        return null;
    }

    /// <summary>
    /// <c>Hook(int index)</c>: the stub tests the sign of <c>ecx</c>, so the arg must be a 32-bit int, and every injected
    /// token must be negative — <c>InjectorHelpers.CreateClassToken</c> decrements <c>s_LastInjectedToken</c> from -2, so a
    /// non-negative value there means the token scheme changed and the sign rule no longer holds.
    /// </summary>
    private static string? CheckTypeDefIndexShape(Type hookType, Delegate detour)
    {
        ParameterInfo[] ps = detour.Method.GetParameters();
        if (ps.Length != 1 || ps[0].ParameterType != typeof(int)) return "detour is not Hook(int)";
        FieldInfo? last = InjectorHelpersType()?.GetField("s_LastInjectedToken", BindingFlags.NonPublic | BindingFlags.Static);
        if (last == null || last.FieldType != typeof(long)) return "InjectorHelpers.s_LastInjectedToken (long) not found";
        long token = (long)last.GetValue(null)!;
        if (token >= 0) return $"injected token counter is non-negative ({token}) — sign rule would misroute";
        return null;
    }

    // ── 2. Addresses ─────────────────────────────────────────────────────────────────────────────────────────────
    // Delegates created by GetDelegateForFunctionPointer hand back their native pointer; the detour delegate hands back
    // its cached reverse-P/Invoke thunk (the same pointer Dobby was given).
    private static string? ResolveAddresses(HookParts p, out Addresses a)
    {
        object hook = p.Hook;
        IntPtr target = Marshal.GetFunctionPointerForDelegate(p.Method);
        IntPtr tramp = Marshal.GetFunctionPointerForDelegate(p.Original);
        IntPtr thunk = Marshal.GetFunctionPointerForDelegate(p.Detour);
        a = new Addresses(target, tramp, thunk);
        // Independent re-derivation of the hooked function (the hook's own export/xref walk → its target).
        var findTarget = hook.GetType().GetMethod("FindTargetMethod", BindingFlags.Public | BindingFlags.Instance);
        if (findTarget == null || (IntPtr)findTarget.Invoke(hook, null)! != target)
            return $"skipped: target cross-check failed (hook says 0x{(long)target:X})";
        if (tramp == IntPtr.Zero || thunk == IntPtr.Zero || tramp == target || tramp == thunk || thunk == target)
            return "skipped: degenerate target/trampoline/thunk addresses";
        if (!ExecutableMemory.IsAccessible(tramp, 16, mustExecute: true) || !ExecutableMemory.IsAccessible(thunk, 16, mustExecute: true))
            return "skipped: trampoline or managed thunk is not executable memory";
        return null;
    }

    // ── 3. Verify Dobby's patch + the trampoline bytes, then emit the stub and swap it in ────────────────────────
    private static string VerifyAndPatch(HookSpec spec, Addresses a)
    {
        (IntPtr target, IntPtr tramp, IntPtr thunk) = (a.Target, a.Tramp, a.Thunk);
        if (!InteropFastPathCode.TryFindDetourSlot(target, thunk, ExecutableMemory.TryRead, out IntPtr slot, out string why))
            return $"skipped: detour layout not recognised: {why}";
        if (!InteropFastPathCode.IsAtomicSlot(slot))
            return $"skipped: detour slot 0x{(long)slot:X} straddles a cache line (write would not be atomic)";
        byte[]? trampBytes = ExecutableMemory.TryRead(tramp, 128) ?? ExecutableMemory.TryRead(tramp, 64);
        if (trampBytes == null || !InteropFastPathCode.HasJumpBack(trampBytes, tramp, target))
            return $"skipped: trampoline 0x{(long)tramp:X} has no jump back into 0x{(long)target:X}" +
                   (trampBytes == null ? "" : $" [{InteropFastPathCode.Hex(trampBytes[..Math.Min(32, trampBytes.Length)])}]");

        IntPtr stub = ExecutableMemory.AllocCode(spec.BuildStub((long)tramp, (long)thunk));
        if (stub == IntPtr.Zero) return "skipped: could not allocate executable memory for the stub";
        if (!ExecutableMemory.CompareExchangePointer(slot, stub, thunk, out string? err))
            return $"skipped: patch failed: {err}";

        return $"installed: {spec.Label} real-type calls bypass the managed hook " +
               $"(target 0x{(long)target:X}, slot 0x{(long)slot:X}, stub 0x{(long)stub:X}, trampoline 0x{(long)tramp:X}, managed 0x{(long)thunk:X})";
    }
}
