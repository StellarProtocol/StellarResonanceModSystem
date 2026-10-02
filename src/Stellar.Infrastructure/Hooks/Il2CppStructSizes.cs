using System;
using Il2CppInterop.Runtime;
namespace Stellar.Infrastructure.Hooks;

/// <summary>The native size of an IL2CPP struct type (an <c>Il2CppSystem.ValueType</c> wrapper) for
/// <see cref="Il2CppPatchSafety"/>: null for any other type, −1 when the size cannot be read (treated as unsafe). Kept apart
/// from <see cref="HarmonyGameMethodHooker"/> so only a real patch loads the interop runtime.</summary>
internal static class Il2CppStructSizes
{
    internal static int? Of(Type type)
    {
        if (!type.IsSubclassOf(typeof(Il2CppSystem.ValueType))) return null;
        try
        {
            var klass = Il2CppClassPointerStore.GetNativeClassPointer(type);
            if (klass == IntPtr.Zero) return -1;
            uint align = 0;
            return IL2CPP.il2cpp_class_value_size(klass, ref align);
        }
        catch { return -1; }
    }
}
