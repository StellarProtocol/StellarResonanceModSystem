using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Repairs Il2CppInterop's IL2CPP type injector on game builds whose <c>GameAssembly.dll</c> codegen
/// makes <c>InjectorHelpers.FindClassInit()</c> mis-resolve the native <c>Class::Init</c> and hard-crash
/// the CLR (<c>0x80131506</c> — an uncatchable <c>ExecutionEngineException</c>) at the first
/// <c>ClassInjector.RegisterTypeInIl2Cpp</c>. Observed on the Steam <c>StarSEA_STEAM</c> build; the
/// standalone <c>StarSEA</c> build resolves correctly.
/// <para>
/// Runs before the framework's first injection and pre-seeds the process-global
/// <c>InjectorHelpers.ClassInit</c> field with Il2CppInterop's own documented substitute export
/// (<c>il2cpp_class_has_references</c>), so the <c>ClassInit ??= FindClassInit()</c> guard skips the
/// broken signature scan. For injected classes the substitute is a harmless no-op (Il2CppInterop builds
/// the class + its other hooks itself) — it is the exact fallback the library uses when its signatures
/// miss. Purely reflective (<c>InjectorHelpers</c> is <c>internal</c>); idempotent; never throws — on any
/// failure it no-ops and the normal scan runs. See <c>docs/recon/steam-client-classinit-injection.md</c>.
/// </para>
/// </summary>
internal static class Il2CppClassInitFix
{
    private const string InjectorHelpersName = "Il2CppInterop.Runtime.Injection.InjectorHelpers";
    private static readonly string[] Substitutes = { "il2cpp_class_has_references", "mono_class_instance_size", "mono_class_setup_vtable" };
    private static bool _seeded;

    /// <summary>Seed <c>InjectorHelpers.ClassInit</c> if not already resolved. Call once, before the first injection.</summary>
    public static void EnsureSeeded(IPluginLog log)
    {
        if (_seeded) return;
        _seeded = true;
        try
        {
            Type? ih = ResolveInjectorHelpers();
            FieldInfo? field = ih?.GetField("ClassInit", BindingFlags.NonPublic | BindingFlags.Static);
            if (ih == null || field == null || field.GetValue(null) != null) return; // not found, or already resolved

            IntPtr ptr = ResolveSubstitute(ih, out string? picked);
            Type? dType = ih.GetNestedType("d_ClassInit", BindingFlags.NonPublic | BindingFlags.Public);
            if (ptr == IntPtr.Zero || dType == null) return;

            field.SetValue(null, Marshal.GetDelegateForFunctionPointer(ptr, dType));
            log.Info($"[ClassInitFix] pre-seeded InjectorHelpers.ClassInit -> '{picked}' (FindClassInit signature scan bypassed)");
        }
        catch (Exception ex)
        {
            log.Debug($"[ClassInitFix] skipped: {ex.Message}");
        }
    }

    private static Type? ResolveInjectorHelpers() =>
        Type.GetType($"{InjectorHelpersName}, Il2CppInterop.Runtime")
        ?? AppDomain.CurrentDomain.GetAssemblies()
              .Select(a => a.GetType(InjectorHelpersName))
              .FirstOrDefault(t => t != null);

    private static IntPtr ResolveSubstitute(Type injectorHelpers, out string? picked)
    {
        picked = null;
        MethodInfo? tryGet = injectorHelpers.GetMethod("TryGetIl2CppExport", BindingFlags.NonPublic | BindingFlags.Static);
        if (tryGet == null) return IntPtr.Zero;
        foreach (string name in Substitutes)
        {
            object[] args = { name, IntPtr.Zero };
            if ((bool)tryGet.Invoke(null, args)!) { picked = name; return (IntPtr)args[1]!; }
        }
        return IntPtr.Zero;
    }
}
