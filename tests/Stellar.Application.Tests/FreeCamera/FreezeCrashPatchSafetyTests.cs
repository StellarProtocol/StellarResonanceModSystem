using System;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Freeze-crash root cause 2026-10-02 (framework e4f925b; report .superpowers/sdd/posing/freeze-crash-rootcause.md): the
// owner's MAIN client died ~1 s after the Photo Studio freeze press. TEST repro: c0000005 inside
// ECSModelResourceManager.PlayClip+0xB67, called through the HarmonyX trampoline — Il2CppInterop reads a by-value IL2CPP
// struct argument (ExternalBlobPtr<T>, 8 bytes, passed BY VALUE in a register on Win64) as a POINTER to the struct, so the
// original got garbage as its clip pointer. Bisect: the same build with PlayClip/PlayDynamicState unpatched froze 30 s
// without a fault.
// Patch-safety follow-up (review of f708b8d, report .superpowers/sdd/posing/patch-safety-report.md): BY-REF blittable
// structs are NOT safe — measured on the game's CoreCLR 6.0.7 (TEST, native entry called with our buffer, unpatched vs
// patched): ref Vector3 / ref Quaternion / ref QualityData reach the original with only their first 8 bytes and come back
// with the caller's first 8 bytes overwritten; ref float makes the trampoline throw so the original never runs. By-VALUE
// blittable structs were byte-identical. Pinned rules:
//  1. Il2CppPatchSafety refuses a by-value IL2CPP struct of 1/2/4/8 bytes (and an unknown size), any by-ref IL2CPP struct,
//     any by-ref value type other than an integer primitive (blittable struct, enum, float, double, char), and an IL2CPP
//     struct RETURN of 1/2/4/8 bytes or unknown size; by-value blittable structs, other struct sizes, integer by-refs and
//     reference types stay patchable;
//  2. a native signature's text is the full name with generic arguments and by-ref (what an exact-signature detour matches);
//  3. ApplyAllData (by-ref QualityData) is a native detour with an exact signature.
// (The ECS play-writer detour pins and the by-ref MoveGo* diagnostics pin were removed 2026-10-02 late with the ECS layer
// gate and the combat diagnostics they pinned — the freeze is a global time pause now; the general refusal rule in 1
// still refuses any such signature.)
// Do not weaken.
public sealed class FreezeCrashPatchSafetyTests
{
    // ---- 1. the patch-safety rule ----

    [Fact]
    public void freeze_crash_by_value_8_byte_il2cpp_struct_is_refused()
    {
        // PlayClip(uint uid, ushort layer, ExternalBlobPtr<AnimationClipBlob> clip, float fade, …): clip = 8 bytes.
        var hazard = Il2CppPatchSafety.FirstHazard(
            new[] { typeof(uint), typeof(ushort), typeof(BlobPtrStandIn), typeof(float), typeof(float) }, typeof(uint), Sizes);
        Assert.Equal(new Il2CppPatchSafety.Finding(2, Il2CppPatchSafety.Hazard.StructInRegister, 8), hazard);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(-1, true)]   // size unreadable: unsafe
    [InlineData(3, false)]
    [InlineData(12, false)]
    [InlineData(16, false)]
    [InlineData(40, false)]   // passed by hidden pointer: the trampoline is right
    public void freeze_crash_only_register_sized_by_value_structs_are_refused(int size, bool refused)
    {
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(uint), typeof(BlobPtrStandIn) }, typeof(void), SizeOf(size));
        Assert.Equal(refused, hazard is not null);
    }

    [Fact]
    public void freeze_crash_by_ref_il2cpp_struct_is_refused_whatever_its_size()
    {
        // ECSAnimController.playECSState(EAnimLayer, ref ECSAnimState, float): ECSAnimState's first field is a uint.
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(int), typeof(StateStandIn).MakeByRefType(), typeof(float) }, typeof(void), Sizes);
        Assert.Equal(new Il2CppPatchSafety.Finding(1, Il2CppPatchSafety.Hazard.StructByRef, 72), hazard);
    }

    [Theory]
    [InlineData(typeof(System.Numerics.Vector3))]      // MoveComp.MoveGo(ref Vector3): x/y came back as a pointer value (6.0.7)
    [InlineData(typeof(System.Numerics.Quaternion))]   // MoveGoByCurve(…, ref Quaternion, …): z/w lost, x/y zeroed (6.0.7)
    [InlineData(typeof(Panda.Utility.Quality.QualityData))]   // ApplyAllData(ref QualityData): bytes 8..95 zeroed (6.0.7)
    [InlineData(typeof(float))]                        // Mathf.SmoothDamp(…, ref float, …): trampoline threw, original skipped (6.0.7)
    [InlineData(typeof(double))]
    [InlineData(typeof(char))]
    [InlineData(typeof(DayOfWeek))]                    // an enum: 8-byte write-back over a 4-byte value
    public void patch_safety_by_ref_non_integer_value_types_are_refused_measured_on_coreclr_6_0_7(Type element)
    {
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(uint), element.MakeByRefType() }, typeof(void), Sizes);
        Assert.Equal(new Il2CppPatchSafety.Finding(1, Il2CppPatchSafety.Hazard.ValueByRef, 0), hazard);
    }

    [Fact]
    public void patch_safety_by_value_blittable_structs_integer_by_refs_and_references_stay_patchable()
    {
        var safe = new[]
        {
            typeof(uint), typeof(int), typeof(float), typeof(System.Numerics.Vector2), typeof(System.Numerics.Vector3),
            typeof(System.Numerics.Quaternion),   // by value: Quaternion.Angle / Vector3.Distance byte-identical patched (6.0.7)
            typeof(string), typeof(object), typeof(object).MakeByRefType(), typeof(string).MakeByRefType(), typeof(DayOfWeek),
            typeof(bool).MakeByRefType(), typeof(int).MakeByRefType(), typeof(uint).MakeByRefType(), typeof(long).MakeByRefType(),
            typeof(byte).MakeByRefType(), typeof(nint).MakeByRefType(),
        };
        Assert.Null(Il2CppPatchSafety.FirstHazard(safe, typeof(void), Sizes));
        Assert.Null(Il2CppPatchSafety.FirstHazard(Array.Empty<Type>(), typeof(System.Numerics.Vector3), Sizes));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(-1, true)]
    [InlineData(12, false)]
    [InlineData(16, false)]
    public void patch_safety_register_sized_il2cpp_struct_returns_are_refused(int size, bool refused)
    {
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(uint) }, typeof(BlobPtrStandIn), SizeOf(size));
        Assert.Equal(refused ? new Il2CppPatchSafety.Finding(Il2CppPatchSafety.ReturnIndex, Il2CppPatchSafety.Hazard.ReturnInRegister, size) : null, hazard);
    }

    [Fact]
    public void patch_safety_a_parameter_hazard_is_reported_before_the_return_type()
    {
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(BlobPtrStandIn) }, typeof(BlobPtrStandIn), Sizes);
        Assert.Equal(0, hazard?.Index);
        Assert.Contains("the return type (BlobPtrStandIn, 8 bytes)",
            Il2CppPatchSafety.Describe(new(Il2CppPatchSafety.ReturnIndex, Il2CppPatchSafety.Hazard.ReturnInRegister, 8), Type.EmptyTypes, typeof(BlobPtrStandIn)));
    }

    [Fact]
    public void freeze_crash_register_sizes_are_exactly_the_win64_by_value_sizes()
    {
        var inRegister = Enumerable.Range(0, 33).Where(Il2CppPatchSafety.PassedInRegister).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8 }, inRegister);
    }

    [Fact]
    public void patch_safety_struct_size_is_null_for_non_il2cpp_types_and_minus_one_when_unreadable()
    {
        bool IsStandIn(Type t) => t == typeof(BlobPtrStandIn) || t == typeof(StateStandIn);
        int? Of(Type t, Func<Type, IntPtr> klass) => Il2CppStructSizes.Of(t, IsStandIn, klass, k => (int)k);
        foreach (var t in new[] { typeof(int), typeof(float), typeof(string), typeof(System.Numerics.Vector3), typeof(DayOfWeek) })
            Assert.Null(Of(t, _ => throw new InvalidOperationException("never asked")));
        Assert.Equal(8, Of(typeof(BlobPtrStandIn), _ => (IntPtr)8));
        Assert.Equal(72, Of(typeof(StateStandIn), _ => (IntPtr)72));
        Assert.Equal(-1, Of(typeof(BlobPtrStandIn), _ => IntPtr.Zero));                                   // class not resolved
        Assert.Equal(-1, Of(typeof(BlobPtrStandIn), _ => throw new TypeInitializationException("x", null)));   // interop threw
    }

    // ---- 2. native signatures ----

    [Fact]
    public void patch_safety_signature_text_is_full_name_generic_args_and_by_ref()
    {
        Assert.Equal("ECSModel.ExternalBlobPtr`1[ECSModel.AnimationClipBlob]", NativeSignature.TypeText(typeof(ECSModel.ExternalBlobPtr<ECSModel.AnimationClipBlob>)));
        Assert.Equal("Unity.Mathematics.float2&", NativeSignature.TypeText(typeof(Unity.Mathematics.float2).MakeByRefType()));
        Assert.Equal("System.Void", NativeSignature.TypeText(typeof(void)));
    }

    // ---- 3. ApplyAllData is a native detour; the by-ref MoveGo* diagnostics hooks are gone ----

    [Fact]
    public void patch_safety_apply_all_data_is_a_native_detour_on_its_exact_signature()
    {
        var abi = DelegateParameters(typeof(QualityApplyDetour))["ApplyAllDataFn"];
        Assert.Equal(new[] { typeof(nint), typeof(byte), typeof(nint) }, abi);   // QualityData*, bool, MethodInfo*
        Assert.Equal(new NativeSignature("System.Void", new[] { "Panda.Utility.Quality.QualityData&", "System.Boolean" }).ToString(),
            QualityApplySignature.Expected.ToString());
        Assert.True(QualityApplySignature.Expected.Matches(Shape("ApplyAllData", 0)));
        Assert.False(QualityApplySignature.Expected.Matches(Shape("ApplyAllData", 1)));   // by value: a different native ABI
        var statics = (string[])typeof(ZRenderQualityBackend).GetField("QualityStatics", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        Assert.DoesNotContain("ApplyAllData", statics);   // never a HarmonyX postfix again
    }

    // ---- helpers ----

    // Stand-ins for IL2CPP struct wrappers: ExternalBlobPtr<T> (one pointer field, 8 bytes) and ECSAnimState (72 bytes).
    private sealed class BlobPtrStandIn { }

    private sealed class StateStandIn { }

    private static int? Sizes(Type t) => t == typeof(BlobPtrStandIn) ? 8 : t == typeof(StateStandIn) ? 72 : null;

    private static Func<Type, int?> SizeOf(int size) => t => t == typeof(BlobPtrStandIn) ? size : null;

    private static System.Collections.Generic.Dictionary<string, Type[]> DelegateParameters(Type owner) =>
        owner.GetNestedTypes(BindingFlags.NonPublic).Where(t => t.IsSubclassOf(typeof(Delegate)))
            .ToDictionary(t => t.Name, t => t.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray());

    // Shape(name, 0) = the exact release_3.7 interop shape; 1.. = a near-miss variant.
    private static MethodInfo Shape(string name, int variant) =>
        typeof(Shapes).GetMethod(variant == 0 ? name : $"{name}_{variant}", BindingFlags.Static | BindingFlags.NonPublic)!;

    private static class Shapes
    {
        internal static void ApplyAllData(ref Panda.Utility.Quality.QualityData data, bool excludeFrameRate) { }
        internal static void ApplyAllData_1(Panda.Utility.Quality.QualityData data, bool excludeFrameRate) { }
    }
}
