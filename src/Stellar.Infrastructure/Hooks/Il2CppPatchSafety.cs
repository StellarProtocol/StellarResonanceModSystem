using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Hooks;

/// <summary>Which IL2CPP method signatures a HarmonyX patch must NEVER wrap — the rule behind the freeze-crash root cause
/// 2026-10-02 (framework e4f925b: the owner's MAIN client died ~1 s after the Photo Studio freeze press; TEST repro run 1
/// faulted <c>c0000005</c> inside <c>ECSModelResourceManager.PlayClip</c>+0xB67, called through the HarmonyX trampoline).
/// Il2CppInterop's native→managed trampoline (<c>Il2CppDetourMethodPatcher.EmitConvertArgumentToManaged</c>, the BepInEx
/// 6 build in use) converts arguments of an IL2CPP struct type (an <c>Il2CppSystem.ValueType</c> wrapper) wrongly:
/// <list type="bullet">
/// <item><b>By value, 1/2/4/8 bytes</b> — it assumes "on x64 a struct is always a pointer" and boxes from the argument as an
/// ADDRESS, but the Win64 ABI passes a struct of exactly 1, 2, 4 or 8 bytes BY VALUE in the register/slot. The original is
/// then called with bytes read from wherever that value points (e.g. <c>ExternalBlobPtr&lt;T&gt;</c>, one pointer field:
/// the first 8 bytes of the blob become the blob pointer) — a native fault in the game, no managed trace. Any other size is
/// passed by hidden pointer, which the trampoline handles.</item>
/// <item><b>By ref</b> — it loads the struct's first 8 bytes as an IL2CPP OBJECT pointer
/// (<c>Il2CppObjectPool.Get</c> → <c>il2cpp_object_get_class</c>), a fault unless that first field happens to be a
/// reference.</item>
/// </list>
/// Such a method needs a native detour with its exact native signature instead (as <c>EcsPlayDetours</c>). Pure: the caller
/// supplies the native struct size of a parameter type (null = not an IL2CPP struct).</summary>
internal static class Il2CppPatchSafety
{
    internal enum Hazard
    {
        /// <summary>A by-value IL2CPP struct of 1/2/4/8 bytes (passed in a register, read as an address).</summary>
        StructInRegister,

        /// <summary>A by-ref IL2CPP struct (its first field read as an object pointer).</summary>
        StructByRef,
    }

    internal readonly record struct Finding(int Index, Hazard Hazard, int Size);

    /// <summary>True when the Win64 ABI passes a struct of <paramref name="size"/> bytes by value in a register/slot.</summary>
    internal static bool PassedInRegister(int size) => size is 1 or 2 or 4 or 8;

    /// <summary>The first parameter HarmonyX must not marshal, or null when the signature is safe to patch.
    /// <paramref name="structSize"/> answers an IL2CPP struct type's native size (−1 = unknown, treated as unsafe) and null for
    /// every other type (primitives, enums, blittable structs, reference types).</summary>
    internal static Finding? FirstHazard(IReadOnlyList<Type> parameterTypes, Func<Type, int?> structSize)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
        {
            var type = parameterTypes[i];
            if (type.IsByRef)
            {
                if (type.GetElementType() is { } element && structSize(element) is int byRefSize)
                    return new Finding(i, Hazard.StructByRef, byRefSize);
                continue;
            }
            if (structSize(type) is int size && (size < 0 || PassedInRegister(size)))
                return new Finding(i, Hazard.StructInRegister, size);
        }
        return null;
    }
}
