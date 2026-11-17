using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Fields;
using Il2CppInterop.Runtime.Runtime;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Repairs a GC hole in Il2CppInterop's type injector: <c>ClassInjector.RegisterTypeInIl2Cpp</c> builds the injected
/// <c>Il2CppClass</c> from a ZEROED struct and marks it <c>Initialized</c>/<c>SizeInited</c> itself, so IL2CPP never
/// runs <c>Class::SetupFields</c> — which is what would have set <c>has_references</c>. It stays 0 even when the
/// parent class holds managed references (e.g. <c>MaskableGraphic</c>: <c>m_Material</c>,
/// <c>m_OnCullStateChanged</c>, <c>m_CanvasRenderer</c>, …).
/// <para>
/// IL2CPP's <c>Object::New</c> (GameAssembly <c>0x9D7920</c>, release_3.7_2) tests <c>[klass+0x135] &amp; 0x20</c>
/// (= <c>has_references</c>) and, when clear, allocates the instance as PTR-FREE (atomic) memory the Boehm GC never
/// scans. Every reference stored in the instance is then invisible: an object held only there (e.g. the
/// <c>UnityEvent</c> Unity's serializer assigns to <c>Graphic.m_OnCullStateChanged</c> — the injected ctor does not
/// run base ctors) is collected and its memory reused → a later serializer walk hits
/// <c>UnityEventBase.DirtyPersistentCalls</c> on freed memory (crash 2026-11-17, <c>GameAssembly+0x6AFA1EA</c>).
/// </para>
/// <para>
/// Fix: right after registration (before any <c>AddComponent</c>), set <c>has_references</c> exactly as IL2CPP's own
/// <c>SetupFields</c> would — <c>parent.has_references || any injected reference field</c>. The bit is written through
/// Il2CppInterop's OWN version-specific class wrapper (<c>UnityVersionHandler.Wrap</c> → its <c>HasReferences</c>
/// property; Unity 2022.3 → <c>NativeClassStructHandler_29_1</c>, <c>_bitfield0</c> at +0x135 bit 5), so the offset is
/// the library's, not hand-coded; then it is read back through the game's <c>il2cpp_class_has_references</c> export
/// (<c>movzx eax, byte [klass+0x135]; shr al,5; and al,1</c>) to prove it landed. <c>gc_desc</c> stays null, so
/// <c>Object::New</c> takes the plain conservatively-scanned allocation (kind NORMAL) — always safe, no typed bitmap
/// to get wrong. Injected MonoBehaviours whose parent has no references (plain tickers) stay 0: unchanged.
/// One-shot per type at registration — no per-frame cost. Never throws.
/// </para>
/// </summary>
internal static class Il2CppInjectedGcFix
{
    /// <summary>Make IL2CPP's GC scan instances of the injected <typeparamref name="T"/> when it (or its parent)
    /// holds managed references. Call once, right after <c>RegisterTypeInIl2Cpp&lt;T&gt;()</c>, before the first
    /// <c>AddComponent&lt;T&gt;</c>. Idempotent; safe if the registration itself threw "already injected".</summary>
    public static void EnsureScanned<T>(Action<string>? log) => EnsureScanned(typeof(T), log);

    public static unsafe void EnsureScanned(Type type, Action<string>? log)
    {
        try
        {
            IntPtr klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (klass == IntPtr.Zero) { log?.Invoke($"[GcFix] {type.Name}: not injected — skipped"); return; }
            if (IL2CPP.il2cpp_class_has_references(klass)) return;   // already scanned (fixed earlier / upstream fix)

            IntPtr parent = IL2CPP.il2cpp_class_get_parent(klass);
            bool wants = (parent != IntPtr.Zero && IL2CPP.il2cpp_class_has_references(parent)) || HasInjectedRefFields(type);
            if (!wants) return;   // e.g. a plain MonoBehaviour ticker: IL2CPP would also leave it pointer-free

            object wrapper = UnityVersionHandler.Wrap((Il2CppClass*)klass);
            PropertyInfo? prop = wrapper.GetType().GetProperty("HasReferences", BindingFlags.Public | BindingFlags.Instance);
            if (prop == null || !prop.CanWrite)
            {
                log?.Invoke($"[GcFix] {type.Name}: Il2CppInterop class wrapper has no HasReferences — GC hazard NOT fixed");
                return;
            }
            prop.SetValue(wrapper, true);
            bool ok = IL2CPP.il2cpp_class_has_references(klass);
            log?.Invoke(ok
                ? $"[GcFix] {type.Name}: has_references 0 -> 1 (instances now GC-scanned)"
                : $"[GcFix] {type.Name}: has_references write did not read back — GC hazard NOT fixed");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[GcFix] {type.Name}: failed ({ex.GetType().Name}: {ex.Message}) — GC hazard NOT fixed");
        }
    }

    // Il2CppInterop injects only these field kinds into the native class; a reference/string one means the instance
    // itself stores IL2CPP references (value-type fields don't). Mirrors IsFieldEligible's reference subset.
    private static bool HasInjectedRefFields(Type type)
    {
        foreach (FieldInfo f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            Type ft = f.FieldType;
            if (ft == typeof(Il2CppStringField)) return true;
            if (ft.IsGenericType && ft.GetGenericTypeDefinition() == typeof(Il2CppReferenceField<>)) return true;
        }
        return false;
    }
}
