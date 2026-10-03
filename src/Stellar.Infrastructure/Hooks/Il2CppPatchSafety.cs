using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Hooks;

/// <summary>Which IL2CPP method signatures a HarmonyX patch must NEVER wrap — the rule behind the freeze-crash root cause
/// 2026-10-02 (framework e4f925b: the owner's MAIN client died ~1 s after the Photo Studio freeze press; TEST repro run 1
/// faulted <c>c0000005</c> inside <c>ECSModelResourceManager.PlayClip</c>+0xB67, called through the HarmonyX trampoline).
/// Il2CppInterop's native→managed trampoline (<c>Il2CppDetourMethodPatcher.EmitConvertArgumentToManaged</c>, the BepInEx
/// 6 build in use) converts these wrongly:
/// <list type="bullet">
/// <item><b>A by-value IL2CPP struct of 1/2/4/8 bytes</b> (an <c>Il2CppSystem.ValueType</c> wrapper) — it boxes from the
/// argument as an ADDRESS, but the Win64 ABI passes a struct of exactly 1, 2, 4 or 8 bytes BY VALUE in the register/slot.
/// The original is then called with bytes read from wherever that value points (<c>ExternalBlobPtr&lt;T&gt;</c>, one pointer
/// field: the first 8 bytes of the blob become the blob pointer) — a native fault, no managed trace. Any other size is passed
/// by hidden pointer, which the trampoline handles.</item>
/// <item><b>The same as a RETURN type</b> — the trampoline returns the managed wrapper's object pointer (sizes 1/4/8) or
/// writes a hidden return buffer the caller never passed (size 2) where the caller expects the value in a register.</item>
/// <item><b>Any by-ref value type other than an integer primitive</b> — it loads the referenced value with one 8-byte
/// <c>ldind.i</c> and writes it back with <c>stind.i</c> unless the element is one of its primitives. Measured on the game's
/// own runtime (TEST prefix, CoreCLR 6.0.7, release_3.7, 2026-10-02; probe = the game method's native entry called with our
/// buffer, unpatched vs patched through this hooker): <c>Vector3.OrthoNormalize(ref Vector3, ref Vector3)</c> — the
/// original saw <c>(3,4,0)</c> for <c>(3,4,12)</c> (only the first 8 bytes reach it) and the caller's x/y came back as a
/// POINTER value (<c>0086D093FF6F0000</c>); <c>QuaternionEqual(ref Quaternion, ref Quaternion)</c> — z/w lost, x/y
/// zeroed in the caller; <c>QualityGradeSetting.ApplyAllData(ref QualityData, bool)</c> — the game APPLIED a 96-byte
/// QualityData with bytes 8..95 zeroed and the caller's first 8 bytes were overwritten; <c>Mathf.SmoothDamp(…, ref float,
/// …)</c> and <c>Quaternion.Internal_ToAxisAngleRad(…, ref Vector3, ref float)</c> — the trampoline threw
/// (NullReferenceException) so the ORIGINAL NEVER RAN and the caller got 0. An IL2CPP struct by ref has its first 8 bytes
/// read as an object pointer (<c>Il2CppObjectPool.Get</c>, a fault unless that field is a reference). Integer primitives
/// (<c>bool</c> … <c>long</c>, <c>nint</c>) are converted correctly and reference types go through the object pool.
/// By-VALUE blittable structs are unaffected (measured: <c>Quaternion.Angle(Q, Q)</c>, <c>Vector3.Distance(V3, V3)</c>
/// byte-identical patched vs unpatched).</item>
/// </list>
/// Such a method needs a native detour with its exact native signature instead (<see cref="NativeDetour"/>). Pure: the caller
/// supplies the native struct size of a type (null = not an IL2CPP struct).</summary>
internal static class Il2CppPatchSafety
{
    internal enum Hazard
    {
        /// <summary>A by-value IL2CPP struct parameter of 1/2/4/8 bytes (passed in a register, read as an address).</summary>
        StructInRegister,

        /// <summary>A by-ref IL2CPP struct (its first field read as an object pointer).</summary>
        StructByRef,

        /// <summary>A by-ref blittable struct, enum, float, double or char (8-byte load/store; measured corruption).</summary>
        ValueByRef,

        /// <summary>An IL2CPP struct RETURN of 1/2/4/8 bytes or unknown size (returned in a register).</summary>
        ReturnInRegister,
    }

    /// <summary>The parameter index (<see cref="ReturnIndex"/> = the return type), the hazard, and the IL2CPP struct size
    /// (0 = not an IL2CPP struct).</summary>
    internal readonly record struct Finding(int Index, Hazard Hazard, int Size);

    /// <summary><see cref="Finding.Index"/> of a return-type hazard.</summary>
    internal const int ReturnIndex = -1;

    // The by-ref element types the trampoline round-trips correctly: the 8-byte load truncates exactly and each has its own
    // store width (bool is passed as byte&). Everything else that is a value type is ValueByRef.
    private static readonly HashSet<Type> SafeByRefValues = new()
    {
        typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(nint), typeof(nuint),
    };

    /// <summary>True when the Win64 ABI passes a struct of <paramref name="size"/> bytes by value in a register/slot.</summary>
    internal static bool PassedInRegister(int size) => size is 1 or 2 or 4 or 8;

    /// <summary>The first hazard HarmonyX must not marshal — parameters in order, then the return type — or null when the
    /// signature is safe to patch. <paramref name="structSize"/> answers an IL2CPP struct type's native size (−1 = unknown,
    /// treated as unsafe) and null for every other type (primitives, enums, blittable structs, reference types).</summary>
    internal static Finding? FirstHazard(IReadOnlyList<Type> parameterTypes, Type returnType, Func<Type, int?> structSize)
    {
        for (var i = 0; i < parameterTypes.Count; i++)
            if (ParameterHazard(parameterTypes[i], structSize) is { } found) return found with { Index = i };
        return structSize(returnType) is int size && InRegisterOrUnknown(size)
            ? new Finding(ReturnIndex, Hazard.ReturnInRegister, size)
            : null;
    }

    private static Finding? ParameterHazard(Type type, Func<Type, int?> structSize)
    {
        if (!type.IsByRef)
            return structSize(type) is int size && InRegisterOrUnknown(size) ? new Finding(0, Hazard.StructInRegister, size) : null;
        if (type.GetElementType() is not { } element) return null;
        if (structSize(element) is int byRefSize) return new Finding(0, Hazard.StructByRef, byRefSize);
        return element.IsValueType && !SafeByRefValues.Contains(element) ? new Finding(0, Hazard.ValueByRef, 0) : null;
    }

    private static bool InRegisterOrUnknown(int size) => size < 0 || PassedInRegister(size);

    /// <summary>"parameter #1 (Vector3&amp;) is a by-ref value type" / "the return type (X, 8 bytes) is …" for the refusal line.</summary>
    internal static string Describe(Finding f, IReadOnlyList<Type> parameterTypes, Type returnType)
    {
        var type = f.Index == ReturnIndex ? returnType : parameterTypes[f.Index];
        var where = f.Index == ReturnIndex ? "the return type" : $"parameter #{f.Index}";
        var size = f.Hazard == Hazard.ValueByRef ? "" : $", {f.Size} bytes";
        var what = f.Hazard switch
        {
            Hazard.StructInRegister => "a register-sized IL2CPP struct by value",
            Hazard.StructByRef => "an IL2CPP struct by ref",
            Hazard.ValueByRef => "a by-ref struct/enum/float value",
            _ => "a register-sized IL2CPP struct return",
        };
        return $"{where} ({type.Name}{size}) is {what} [{f.Hazard}]";
    }
}
