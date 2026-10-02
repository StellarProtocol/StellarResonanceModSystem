using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The interception behind <see cref="EcsSpeedGate"/>: the ECS animator's four speed writers on
/// <c>ECSModel.ECSModelResourceManager</c> (release_3.7 interop + ISIL; native caller counts in brackets) —
/// <c>SetAnimatorLayerData(uint uid, int layer, float speed, float weight)</c> [9], <c>PlayState(uid, ushort layer,
/// uint hash, ref float2, float normalizedTime, float fade, float speed, float weight, …)</c> [11],
/// <c>PlayClip(uid, ushort layer, clip, float fade, float normalizedTime, float speed, float weight, …)</c> [3] and
/// <c>PlayDynamicState(uid, ushort layer, state, float2, float normalizedTime, float fade, float speed, float weight, …)</c>
/// [1]. Every one is static and takes the ECS uid first. Never skips the original (the play still happens, frozen at its
/// first frame). A method that does not resolve or install is skipped and logged; the others still install. Never
/// <c>tryCalculateAnimSpeed</c> (R7-6).
/// <para><b>Two interception paths (freeze-crash root cause 2026-10-02, framework e4f925b).</b> Only
/// <c>SetAnimatorLayerData</c> — primitives only — is a HarmonyX prefix (a dedicated static method taking the uid, the
/// layer, <c>ref</c> the speed and the weight by position: no <c>__args</c>, no boxing). The three PLAY writers take an
/// IL2CPP struct (<c>ExternalBlobPtr&lt;T&gt;</c>, 8 bytes, by value; <c>PlayState</c>'s <c>in float2</c>) that
/// Il2CppInterop's HarmonyX trampoline mis-marshals: it reads a by-value struct argument as a POINTER to the struct, while
/// the Win64 ABI passes an 8-byte struct by value in a register — so the game's original <c>PlayClip</c> received the first
/// 8 bytes of the clip blob as its clip pointer and faulted (<c>c0000005</c> inside <c>PlayClip</c>+0xB67, called through
/// the trampoline; the owner's MAIN client died ~1 s after the freeze press). They are native detours with the exact
/// native signature instead (<see cref="EcsPlayDetours"/>); <see cref="Il2CppPatchSafety"/> makes the hooker refuse any
/// such HarmonyX patch.</para>
/// <para><b>Static state — the HarmonyX exception</b> (as <see cref="DrawnSpeedPatch"/>): <c>s_gate</c>, written once at
/// install on the main thread, read-only afterwards, holding no game object.</para></summary>
internal static class EcsSpeedPatch
{
    internal const string ManagerType = "ECSModel.ECSModelResourceManager";

    private static EcsSpeedGate? s_gate;

    /// <summary>The exact interop signature of the HarmonyX target (primitives only: no Il2CppInterop hazard).</summary>
    internal static readonly NativeSignature LayerDataSignature =
        new("System.Void", new[] { "System.UInt32", "System.Int32", "System.Single", "System.Single" });

    /// <summary>The writers patched through HarmonyX: only the primitive-only <c>SetAnimatorLayerData</c>.</summary>
    internal static readonly string[] HarmonyTargets = { "SetAnimatorLayerData" };

    /// <summary>The writers taking an IL2CPP struct argument: native detours, NEVER HarmonyX (freeze-crash 2026-10-02).</summary>
    internal static readonly string[] NativeTargets = { "PlayState", "PlayClip", "PlayDynamicState" };

    /// <summary>What <see cref="Install"/> put live: the HarmonyX prefix, the native detours, and the writers left alone.</summary>
    internal sealed record Installed(List<string> Harmony, List<string> Native, List<string> Skipped)
    {
        internal int Count => Harmony.Count + Native.Count;

        public override string ToString() =>
            $"harmony=[{string.Join(",", Harmony)}] native=[{string.Join(",", Native)}] skipped=[{string.Join(",", Skipped)}]";
    }

    /// <summary>Installs every writer that resolves with its exact signature. A writer whose signature changed (game patch)
    /// is not hooked and reported through <paramref name="error"/>; any other install failure through <paramref name="warn"/>.</summary>
    internal static Installed Install(HarmonyGameMethodHooker hooker, Type manager, EcsSpeedGate gate, Action<string> warn, Action<string> error)
    {
        s_gate = gate;
        var done = new Installed(new List<string>(), new List<string>(), new List<string>());
        var prefix = typeof(EcsSpeedPatch).GetMethod(nameof(LayerData), BindingFlags.Static | BindingFlags.NonPublic);
        var layerData = Resolve(manager, HarmonyTargets[0], error);
        (prefix is not null && layerData is not null && hooker.PrefixWith(layerData, prefix) ? done.Harmony : done.Skipped).Add(HarmonyTargets[0]);
        foreach (var name in NativeTargets)
            (Resolve(manager, name, error) is { } play && EcsPlayDetours.Install(name, play, warn) ? done.Native : done.Skipped).Add(name);
        return done;
    }

    private static MethodInfo? Resolve(Type manager, string name, Action<string> error)
    {
        var named = manager.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == name).ToArray();
        if (named.FirstOrDefault(m => FitsTarget(name, m)) is { } exact) return exact;
        error($"ECS {name} not hooked: no overload has the exact signature {SignatureOf(name)} " +
              $"(found: {(named.Length == 0 ? "none" : string.Join(" | ", named.Select(NativeSignature.Of)))})");
        return null;
    }

    /// <summary>The exact signature <paramref name="name"/> must carry, or null for a name that is not a writer.</summary>
    internal static NativeSignature? SignatureOf(string name) =>
        name == HarmonyTargets[0] ? LayerDataSignature : EcsPlaySignatures.Expected.TryGetValue(name, out var s) ? s : null;

    /// <summary>True when <paramref name="m"/> is the writer <paramref name="name"/> with its FULL expected signature —
    /// every parameter type and the return type (a native detour's delegate is the ABI; review 2026-10-02).</summary>
    internal static bool FitsTarget(string name, MethodInfo m) =>
        m.Name == name && SignatureOf(name) is { } expected && expected.Matches(m);

    // ---- the HarmonyX prefix (static, by position; never throws into the native caller) ----

    private static void LayerData(uint __0, int __1, ref float __2, float __3) => Ask(EcsSpeedGate.Writer.LayerData, __0, __1, ref __2, __3);

    /// <summary>The gate's question for one writer call — the HarmonyX prefix and the native play detours both ask it.
    /// Unarmed: a bare early-out (two field reads). Armed: the diagnostics-only call counter, then the gate.</summary>
    internal static void Ask(EcsSpeedGate.Writer writer, uint uid, int layer, ref float speed, float weight)
    {
        var gate = s_gate;
        if (gate is null || !gate.Armed) return;
        try
        {
            gate.CountCall(writer);
            gate.TrySubstitute(uid, layer, ref speed, weight, Environment.CurrentManagedThreadId);
        }
        catch { /* trust boundary */ }
    }
}
