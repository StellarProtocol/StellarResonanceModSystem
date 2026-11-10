using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Native early-out in front of Il2CppInterop's <c>Class_FromIl2CppType_Hook</c>.
/// <para>
/// That hook (installed by <c>InjectorHelpers.Setup</c> at the first type injection, process-global, never removed)
/// detours native <c>il2cpp::vm::Class::FromIl2CppType</c> to a MANAGED delegate. The game calls that function
/// constantly (variant-generic <c>IsAssignableFrom</c> on <c>is</c>/<c>as</c>, per entity per frame), and every call
/// paid a reverse-P/Invoke + forward-P/Invoke round trip even though only fake injected-type tokens (negative
/// <c>Il2CppType.data</c>) need the managed code — measured ~10–12% of the raid main thread (~4–5 ms/frame).
/// </para>
/// <para>
/// Fix: a 45-byte native stub (<see cref="InteropFastPathCode.BuildStub"/>) that tests the sign of <c>type-&gt;data</c>
/// and jumps either to the ORIGINAL function's trampoline (non-negative: what the managed hook would have done anyway)
/// or to the managed hook's thunk (negative: unchanged path). A null-page <c>type</c> pointer is never dereferenced
/// natively — it goes to the managed hook so its fault stays a catchable NRE, not a process-killing AV. Installed by swapping Dobby's detour pointer slot from the
/// managed thunk to the stub with one atomic compare-and-swap. Every address is cross-checked first; any mismatch or
/// exception skips with one log line and leaves the original detour in place. Same family as
/// <see cref="Il2CppClassInitFix"/> (reflective repair of Il2CppInterop's injector internals).
/// </para>
/// Kill switch: <c>STELLAR_NO_INTEROP_FASTPATH=1</c> or a <c>NO_INTEROP_FASTPATH</c> line in <c>stellar_perf.flags</c>.
/// </summary>
internal static class Il2CppInteropFastPath
{
    /// <summary>Launch flag (<see cref="PerfControls.Flag"/>) that disables the fast path.</summary>
    public const string KillSwitchFlag = "NO_INTEROP_FASTPATH";

    private const string HookTypeName = "Il2CppInterop.Runtime.Injection.Hooks.Class_FromIl2CppType_Hook";
    private const string Tag = "[InteropFastPath]";

    private enum State { NotTried, Pending, Done }
    private static State _state;

    /// <summary>
    /// Install once (main thread, framework startup, after the first type injection). If Il2CppInterop has not
    /// applied its hook yet, stays pending and <see cref="RetryIfPending"/> tries exactly once more. Never throws.
    /// </summary>
    public static void TryInstall(IPluginLog log)
    {
        if (_state == State.Done) return;
        bool lastChance = _state == State.Pending;
        _state = State.Done;
        try
        {
            if (PerfControls.Flag(KillSwitchFlag))
            {
                log.Info($"{Tag} skipped: disabled by the {KillSwitchFlag} launch flag");
                return;
            }
            string result = Install(out bool notYet);
            if (notYet && !lastChance)
            {
                _state = State.Pending;
                log.Debug($"{Tag} hook not applied yet — will retry once on the first ticker frame");
                return;
            }
            if (result.StartsWith("installed", StringComparison.Ordinal)) log.Info($"{Tag} {result}");
            else log.Warning($"{Tag} {result}");
        }
        catch (Exception ex)
        {
            log.Warning($"{Tag} skipped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Second (final) attempt when the first found no hook yet. No-op otherwise.</summary>
    public static void RetryIfPending(IPluginLog log)
    {
        if (_state == State.Pending) TryInstall(log);
    }

    private static string Install(out bool notYet)
    {
        notYet = false;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return "skipped: not Windows x64";
        string? fail = ReadHook(out HookParts parts, out notYet);
        if (fail != null) return fail;
        fail = ResolveAddresses(parts, out Addresses a);
        if (fail != null) return fail;
        return VerifyAndPatch(a.Target, a.Tramp, a.Thunk);
    }

    // ── 1. Il2CppInterop internals (reflection; internal API — every shape assumption is checked) ─────────────────
    private readonly record struct HookParts(object Hook, Delegate Detour, Delegate Original, Delegate Method);
    private readonly record struct Addresses(IntPtr Target, IntPtr Tramp, IntPtr Thunk);

    private static string? ReadHook(out HookParts parts, out bool notYet)
    {
        parts = default;
        notYet = false;
        Type? ih = typeof(ClassInjector).Assembly.GetType("Il2CppInterop.Runtime.Injection.InjectorHelpers");
        object? hook = ih?.GetField("FromIl2CppTypeHook", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        if (hook == null) return "skipped: InjectorHelpers.FromIl2CppTypeHook not found (Il2CppInterop changed?)";
        Type hookType = hook.GetType();
        if (hookType.FullName != HookTypeName) return $"skipped: unexpected hook type {hookType.FullName}";

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
        if (!(bool)fApplied.GetValue(hook)!) { notYet = true; return "skipped: Class::FromIl2CppType hook was never applied"; }

        var detour = fDetour.GetValue(hook) as Delegate;
        var original = fOriginal.GetValue(hook) as Delegate;
        var method = fMethod.GetValue(hook) as Delegate;
        if (detour == null || original == null || method == null) return "skipped: hook delegates are null";

        // The managed body must be the one whose semantics the stub mirrors (negative data → lookup, else Original).
        if (detour.Method.Name != "Hook" || detour.Method.DeclaringType != hookType || detour.Method.GetParameters().Length != 2)
            return $"skipped: unexpected detour method {detour.Method.DeclaringType?.Name}.{detour.Method.Name}";
        Type? typeStruct = hookType.GetNestedType("Il2CppType", BindingFlags.NonPublic | BindingFlags.Public);
        if (typeStruct == null || Marshal.OffsetOf(typeStruct, "data") != IntPtr.Zero)
            return "skipped: Il2CppType.data is not at offset 0";
        parts = new HookParts(hook, detour, original, method);
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
        // Independent re-derivation of the hooked function (export il2cpp_class_from_il2cpp_type → its jmp target).
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
    private static string VerifyAndPatch(IntPtr target, IntPtr tramp, IntPtr thunk)
    {
        if (!InteropFastPathCode.TryFindDetourSlot(target, thunk, ExecutableMemory.TryRead, out IntPtr slot, out string why))
            return $"skipped: detour layout not recognised: {why}";
        if (!InteropFastPathCode.IsAtomicSlot(slot))
            return $"skipped: detour slot 0x{(long)slot:X} straddles a cache line (write would not be atomic)";
        byte[]? trampBytes = ExecutableMemory.TryRead(tramp, 128) ?? ExecutableMemory.TryRead(tramp, 64);
        if (trampBytes == null || !InteropFastPathCode.HasJumpBack(trampBytes, tramp, target))
            return $"skipped: trampoline 0x{(long)tramp:X} has no jump back into 0x{(long)target:X}" +
                   (trampBytes == null ? "" : $" [{InteropFastPathCode.Hex(trampBytes[..Math.Min(32, trampBytes.Length)])}]");

        IntPtr stub = ExecutableMemory.AllocCode(InteropFastPathCode.BuildStub((long)tramp, (long)thunk));
        if (stub == IntPtr.Zero) return "skipped: could not allocate executable memory for the stub";
        if (!ExecutableMemory.CompareExchangePointer(slot, stub, thunk, out string? err))
            return $"skipped: patch failed: {err}";

        return $"installed: Class::FromIl2CppType real-type calls bypass the managed hook " +
               $"(target 0x{(long)target:X}, slot 0x{(long)slot:X}, stub 0x{(long)stub:X}, trampoline 0x{(long)tramp:X}, managed 0x{(long)thunk:X})";
    }
}
