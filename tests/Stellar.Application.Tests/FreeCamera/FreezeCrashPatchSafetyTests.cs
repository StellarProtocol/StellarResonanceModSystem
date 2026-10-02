using System;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Hooks;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Freeze-crash root cause 2026-10-02 (framework e4f925b; report .superpowers/sdd/posing/freeze-crash-rootcause.md): the
// owner's MAIN client died ~1 s after the Photo Studio freeze press. TEST repro: c0000005 inside
// ECSModelResourceManager.PlayClip+0xB67, called through the HarmonyX trampoline — Il2CppInterop reads a by-value IL2CPP
// struct argument (ExternalBlobPtr<T>, 8 bytes, passed BY VALUE in a register on Win64) as a POINTER to the struct, so the
// original got garbage as its clip pointer. Bisect: the same build with PlayClip/PlayDynamicState unpatched froze 30 s
// without a fault. Pinned rules:
//  1. Il2CppPatchSafety refuses a by-value IL2CPP struct of 1/2/4/8 bytes (and an unknown size) and any by-ref IL2CPP
//     struct; everything else (primitives, blittable structs, other sizes, reference types) stays patchable;
//  2. the ECS PLAY writers are never HarmonyX targets — only the primitive-only SetAnimatorLayerData is; the plays are native
//     detours.
// Do not weaken.
public sealed class FreezeCrashPatchSafetyTests
{
    // ---- 1. the patch-safety rule ----

    [Fact]
    public void freeze_crash_by_value_8_byte_il2cpp_struct_is_refused()
    {
        // PlayClip(uint uid, ushort layer, ExternalBlobPtr<AnimationClipBlob> clip, float fade, …): clip = 8 bytes.
        var hazard = Il2CppPatchSafety.FirstHazard(
            new[] { typeof(uint), typeof(ushort), typeof(BlobPtrStandIn), typeof(float), typeof(float) }, Sizes);
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
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(uint), typeof(BlobPtrStandIn) }, t => t == typeof(BlobPtrStandIn) ? size : null);
        Assert.Equal(refused, hazard is not null);
    }

    [Fact]
    public void freeze_crash_by_ref_il2cpp_struct_is_refused_whatever_its_size()
    {
        // ECSAnimController.playECSState(EAnimLayer, ref ECSAnimState, float): ECSAnimState's first field is a uint.
        var hazard = Il2CppPatchSafety.FirstHazard(new[] { typeof(int), typeof(StateStandIn).MakeByRefType(), typeof(float) }, Sizes);
        Assert.Equal(new Il2CppPatchSafety.Finding(1, Il2CppPatchSafety.Hazard.StructByRef, 72), hazard);
    }

    [Fact]
    public void freeze_crash_primitives_blittable_structs_and_references_stay_patchable()
    {
        var safe = new[]
        {
            typeof(uint), typeof(int), typeof(float), typeof(float).MakeByRefType(), typeof(System.Numerics.Vector2),
            typeof(string), typeof(object), typeof(object).MakeByRefType(), typeof(DayOfWeek),
        };
        Assert.Null(Il2CppPatchSafety.FirstHazard(safe, Sizes));
        Assert.Null(Il2CppPatchSafety.FirstHazard(Array.Empty<Type>(), Sizes));
    }

    [Fact]
    public void freeze_crash_register_sizes_are_exactly_the_win64_by_value_sizes()
    {
        var inRegister = Enumerable.Range(0, 33).Where(Il2CppPatchSafety.PassedInRegister).ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8 }, inRegister);
    }

    // ---- 2. the ECS play writers are never HarmonyX targets ----

    [Fact]
    public void freeze_crash_ecs_play_writers_are_native_detours_never_harmony_targets()
    {
        Assert.Equal(new[] { "SetAnimatorLayerData" }, EcsSpeedPatch.HarmonyTargets);
        Assert.Equal(new[] { "PlayState", "PlayClip", "PlayDynamicState" }, EcsSpeedPatch.NativeTargets);
        Assert.Empty(EcsSpeedPatch.HarmonyTargets.Intersect(EcsSpeedPatch.NativeTargets));
    }

    [Fact]
    public void freeze_crash_ecs_play_detours_mirror_the_native_signatures()
    {
        // The detour delegates are the native ABI: struct arguments as machine words, speed at the interop position, and
        // IL2CPP's hidden MethodInfo* last (release_3.7 ISIL of ECSAnimState.Play).
        var delegates = typeof(EcsPlayDetours).GetNestedTypes(BindingFlags.NonPublic).Where(t => t.IsSubclassOf(typeof(Delegate)))
            .ToDictionary(t => t.Name, t => t.GetMethod("Invoke")!.GetParameters().Select(p => p.ParameterType).ToArray());
        Assert.Equal(new[] { typeof(uint), typeof(ushort), typeof(uint), typeof(nint), typeof(float), typeof(float), typeof(float), typeof(float), typeof(int), typeof(float), typeof(nint) },
            delegates["PlayStateFn"]);
        Assert.Equal(new[] { typeof(uint), typeof(ushort), typeof(nint), typeof(float), typeof(float), typeof(float), typeof(float), typeof(int), typeof(float), typeof(nint) },
            delegates["PlayClipFn"]);
        Assert.Equal(new[] { typeof(uint), typeof(ushort), typeof(nint), typeof(long), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float), typeof(nint) },
            delegates["PlayDynamicFn"]);
        Assert.All(delegates.Values, ps => Assert.DoesNotContain(ps, t => !t.IsPrimitive));
    }

    // ---- helpers ----

    // Stand-ins for IL2CPP struct wrappers: ExternalBlobPtr<T> (one pointer field, 8 bytes) and ECSAnimState (72 bytes).
    private sealed class BlobPtrStandIn { }

    private sealed class StateStandIn { }

    private static int? Sizes(Type t) => t == typeof(BlobPtrStandIn) ? 8 : t == typeof(StateStandIn) ? 72 : null;
}
