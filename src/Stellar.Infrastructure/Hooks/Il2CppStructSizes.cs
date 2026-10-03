using System;
using Il2CppInterop.Runtime;
namespace Stellar.Infrastructure.Hooks;

/// <summary>The native size of an IL2CPP struct type (an <c>Il2CppSystem.ValueType</c> wrapper) for
/// <see cref="Il2CppPatchSafety"/>: null for any other type, −1 when the size cannot be read (treated as unsafe). Kept apart
/// from <see cref="HarmonyGameMethodHooker"/> so only a real patch loads the interop runtime.</summary>
internal static class Il2CppStructSizes
{
    internal static int? Of(Type type) =>
        Of(type, t => t.IsSubclassOf(typeof(Il2CppSystem.ValueType)), Il2CppClassPointerStore.GetNativeClassPointer, ValueSize);

    /// <summary>The rule, with the interop runtime's three answers supplied (pure; unit-tested): null unless
    /// <paramref name="isIl2CppStruct"/>; −1 when the class pointer is unresolved (0) or any step throws.</summary>
    internal static int? Of(Type type, Func<Type, bool> isIl2CppStruct, Func<Type, IntPtr> classPointer, Func<IntPtr, int> valueSize)
    {
        try
        {
            if (!isIl2CppStruct(type)) return null;
            var klass = classPointer(type);
            return klass == IntPtr.Zero ? -1 : valueSize(klass);
        }
        catch { return -1; }
    }

    private static int ValueSize(IntPtr klass)
    {
        uint align = 0;
        return IL2CPP.il2cpp_class_value_size(klass, ref align);
    }
}
