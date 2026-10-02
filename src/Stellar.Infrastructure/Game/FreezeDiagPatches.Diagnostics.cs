using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostics-only HarmonyX prefixes on the candidate ALTERNATIVE drivers of a frozen monster (owner report
/// 2026-10-02: in combat, frozen monsters keep animating, walking / sliding, and their skill effects keep playing). Chosen
/// from the release_3.7 interop (ilspycmd; native caller counts in brackets): the anim controller behind the gated drawn
/// speed — <c>ECSAnimController.set_Speed</c> [6], <c>playECSState</c> [3+1]; <c>ZAnimController.set_Speed</c> [9] /
/// <c>set_PauseGraph</c> [11] for GameObject (Animancer) models; <c>AnimCompBase.PlayBaseState / PlayUpperState /
/// PlayManualClip</c> [4 each]; the skill timeline <c>ZSkillShow.PlaySkillStage</c> [6] / <c>PlayVehicleShow</c> [2] (by the
/// caster-uuid argument); the skill state <c>ZStateSkillComp.Update / motionUpdate / doSkillStage / stageInitAnim /
/// correctPosition</c>; the move state <c>ZStateMoveComp.Update</c>; the drawn-transform movers <c>MoveComp.SimpleMoveGo /
/// RotGo / SimpleRotGo</c> (NOT <c>MoveGo / MoveGoByCurve / MoveGoBySpeed</c>: they take a <c>ref Vector3</c> /
/// <c>ref Quaternion</c>, which the HarmonyX trampoline corrupts — measured 2026-10-02, <see cref="Hooks.Il2CppPatchSafety"/>;
/// the hooker refuses them); the drawn position itself
/// (<c>ECSModelGoComp</c> / <c>ModelGoComp.set_Position</c>, virtual overrides — before or after our hold write); and the
/// effect paths beside the hooked <c>AddEffectDisplay(ZEffect)</c>: <c>ZEffect.Init</c>, <c>ZEffectManager.AddEffect</c>,
/// <c>SetEffectFreeze(false)</c>, <c>SetEffectSpeed / SetEffectSpeedScale</c>. Never <c>tryCalculateAnimSpeed</c> (hung the
/// main thread, R7-6). Each prefix is a dedicated static method taking only what it needs by position (no <c>__args</c>
/// array, no boxing of the arguments), returns after one field read unless a freeze is being sampled, never skips the
/// original and never throws into native code. Installed once, on the first freeze, only with diagnostics on, and not at
/// all with <c>STELLAR_FREEZE_DIAG_HOOKS=0</c> (the kill switch if any hook misbehaves on the owner's client).
/// <para><b>Static state — the HarmonyX exception</b> (same as <see cref="DrawnSpeedPatch"/>): a patch method is static, so
/// the one sink and the method table are static fields, written on the main thread at install and per freeze.</para></summary>
internal static class FreezeDiagPatches
{
    internal const string KillSwitch = "STELLAR_FREEZE_DIAG_HOOKS";

    private static FreezeDiagSink? s_sink;
    private static readonly Dictionary<MethodBase, int> s_ids = new();
    private static readonly List<(string Name, DiagSlot Slot)> s_meta = new();
    private static int[] s_hits = Array.Empty<int>();

    /// <summary>(type, method, prefix, slot): every overload of the method gets the prefix.</summary>
    private static readonly (string Type, string Method, string Prefix, DiagSlot Slot)[] Targets =
    {
        ("Panda.ZGame.ECSAnimController", "set_Speed", nameof(ControllerSpeed), DiagSlot.CtlSpeed),
        ("Panda.ZAnim.ZAnimController", "set_Speed", nameof(ControllerSpeed), DiagSlot.CtlSpeed),
        ("Panda.ZAnim.ZAnimController", "set_PauseGraph", nameof(Pointer), DiagSlot.CtlPause),
        ("Panda.ZGame.ECSAnimController", "playECSState", nameof(Pointer), DiagSlot.CtlPlay),
        (DrawnSpeedPatch.AnimCompType, "PlayBaseState", nameof(Pointer), DiagSlot.AnimPlay),
        (DrawnSpeedPatch.AnimCompType, "PlayUpperState", nameof(Pointer), DiagSlot.AnimPlay),
        (DrawnSpeedPatch.AnimCompType, "PlayManualClip", nameof(Pointer), DiagSlot.AnimPlay),
        ("Panda.ZGame.ZSkillShow", "PlaySkillStage", nameof(Caster), DiagSlot.SkillStage),
        ("Panda.ZGame.ZSkillShow", "PlayVehicleShow", nameof(Caster), DiagSlot.SkillStage),
        ("Panda.ZGame.ZStateSkillComp", "Update", nameof(Host), DiagSlot.SkillTick),
        ("Panda.ZGame.ZStateSkillComp", "motionUpdate", nameof(Host), DiagSlot.SkillTick),
        ("Panda.ZGame.ZStateSkillComp", "doSkillStage", nameof(Host), DiagSlot.SkillStep),
        ("Panda.ZGame.ZStateSkillComp", "stageInitAnim", nameof(Host), DiagSlot.SkillStep),
        ("Panda.ZGame.ZStateSkillComp", "correctPosition", nameof(Host), DiagSlot.SkillStep),
        ("Panda.ZGame.ZStateMoveComp", "Update", nameof(Host), DiagSlot.MoveTick),
        ("Panda.ZGame.MoveComp", "SimpleMoveGo", nameof(Host), DiagSlot.MoveGo),
        ("Panda.ZGame.MoveComp", "RotGo", nameof(Host), DiagSlot.MoveGo),
        ("Panda.ZGame.MoveComp", "SimpleRotGo", nameof(Host), DiagSlot.MoveGo),
        ("Panda.ZGame.ECSModelGoComp", "set_Position", nameof(Position), DiagSlot.GoPosBefore),
        ("Panda.ZGame.ModelGoComp", "set_Position", nameof(Position), DiagSlot.GoPosBefore),
        ("Panda.ZEffect.ZEffect", "Init", nameof(Global), DiagSlot.FxInit),
        ("Panda.ZEffect.ZEffect", "SetEffectFreeze", nameof(EffectFreezeSelf), DiagSlot.FxUnfreeze),
        (GameFreezeBackend.EffectManagerType, "SetEffectFreeze", nameof(EffectFreezeByUid), DiagSlot.FxUnfreeze),
        (GameFreezeBackend.EffectManagerType, "AddEffect", nameof(Global), DiagSlot.FxAdd),
        (GameFreezeBackend.EffectManagerType, "SetEffectSpeed", nameof(Global), DiagSlot.FxSpeed),
        (GameFreezeBackend.EffectManagerType, "SetEffectSpeedScale", nameof(Global), DiagSlot.FxSpeed),
    };

    /// <summary>Patches every target that resolves; returns the names patched (and logs the rest as skipped).</summary>
    internal static List<string> Install(HarmonyGameMethodHooker hooker, IGameTypeRegistry types, FreezeDiagSink sink, IPluginLog log)
    {
        s_sink = sink;
        var patched = new List<string>();
        if (Environment.GetEnvironmentVariable(KillSwitch) == "0") { log.Info($"[FreeCamDiag] hooks off ({KillSwitch}=0)"); return patched; }
        foreach (var (typeName, methodName, prefixName, slot) in Targets)
        {
            var type = types.FindType(typeName);
            var prefix = typeof(FreezeDiagPatches).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var methods = type?.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == methodName && !m.IsAbstract && !m.IsGenericMethodDefinition && Fits(prefixName, m)).ToArray();
            if (type is null || prefix is null || methods is null || methods.Length == 0)
            {
                log.Info($"[FreeCamDiag] hook skipped: {typeName}.{methodName} (not found on this client)");
                continue;
            }
            foreach (var m in methods)
                if (Register(m, $"{type.Name}.{methodName}", slot) && hooker.PrefixWith(m, prefix)) patched.Add($"{type.Name}.{methodName}");
        }
        s_hits = new int[s_meta.Count];
        return patched;
    }

    /// <summary>Per-freeze reset of the per-method hit counts.</summary>
    internal static void Reset() => Array.Clear(s_hits, 0, s_hits.Length);

    /// <summary>"name=n …" for every patched method (0 included — a hook that never fired is visible, § 15).</summary>
    internal static string HitsText()
    {
        var byName = new SortedDictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < s_meta.Count && i < s_hits.Length; i++)
            byName[s_meta[i].Name] = (byName.TryGetValue(s_meta[i].Name, out var n) ? n : 0) + s_hits[i];
        return string.Join(" ", byName.Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static bool Register(MethodBase m, string name, DiagSlot slot)
    {
        if (s_ids.ContainsKey(m)) return false;
        s_ids[m] = s_meta.Count;
        s_meta.Add((name, slot));
        return true;
    }

    // The by-position arguments each prefix takes must exist on the overload, with the right type.
    private static bool Fits(string prefix, MethodInfo m)
    {
        var ps = m.GetParameters();
        return prefix switch
        {
            nameof(ControllerSpeed) => ps.Length == 1 && ps[0].ParameterType == typeof(float),
            nameof(Caster) => ps.Length >= 3 && ps[0].ParameterType == typeof(int) && ps[1].ParameterType == typeof(int) && ps[2].ParameterType == typeof(long),
            nameof(EffectFreezeSelf) => ps.Length == 1 && ps[0].ParameterType == typeof(bool),
            nameof(EffectFreezeByUid) => ps.Length == 2 && ps[1].ParameterType == typeof(bool),
            nameof(Position) => ps.Length == 1,
            _ => true,
        };
    }

    /// <summary>The method's slot on the main thread while sampling; counts the per-method hit and notes the first.</summary>
    private static bool Enter(MethodBase original, out FreezeDiagSink sink, out DiagSlot slot)
    {
        sink = s_sink!;
        slot = default;
        if (sink is null || !sink.Enter() || !s_ids.TryGetValue(original, out var id)) return false;
        slot = s_meta[id].Slot;
        if (id < s_hits.Length && s_hits[id]++ == 0) sink.First(s_meta[id].Name);
        return true;
    }

    // ---- the prefixes (static, by position; never throw) ----

    private static void ControllerSpeed(object __instance, MethodBase __originalMethod, float __0)
    {
        try
        {
            if (!Enter(__originalMethod, out var sink, out _)) return;
            sink.Hit(__instance, DiagSlot.CtlSpeed);
            if (__0 > FreezeLedger.SpeedEpsilon) sink.Hit(__instance, DiagSlot.CtlSpeedUp);
        }
        catch { /* trust boundary */ }
    }

    private static void Pointer(object __instance, MethodBase __originalMethod)
    {
        try { if (Enter(__originalMethod, out var sink, out var slot)) sink.Hit(__instance, slot); }
        catch { /* trust boundary */ }
    }

    private static void Host(object __instance, MethodBase __originalMethod)
    {
        try { if (Enter(__originalMethod, out var sink, out var slot)) sink.HitHost(__instance, slot); }
        catch { /* trust boundary */ }
    }

    private static void Caster(MethodBase __originalMethod, int __0, int __1, long __2)
    {
        try
        {
            if (!Enter(__originalMethod, out var sink, out var slot)) return;
            sink.Hit(__2, slot);
            sink.NoteStage(__2, __0, __1);
        }
        catch { /* trust boundary */ }
    }

    private static void Position(object __instance, MethodBase __originalMethod)
    {
        try { if (Enter(__originalMethod, out var sink, out _)) sink.HitPosition(__instance); }
        catch { /* trust boundary */ }
    }

    private static void Global(MethodBase __originalMethod)
    {
        try { if (Enter(__originalMethod, out var sink, out var slot)) sink.Hit(0L, slot); }
        catch { /* trust boundary */ }
    }

    private static void EffectFreezeSelf(MethodBase __originalMethod, bool __0)
    {
        try { if (!__0 && Enter(__originalMethod, out var sink, out var slot)) sink.Hit(0L, slot); }
        catch { /* trust boundary */ }
    }

    private static void EffectFreezeByUid(MethodBase __originalMethod, bool __1)
    {
        try { if (!__1 && Enter(__originalMethod, out var sink, out var slot)) sink.Hit(0L, slot); }
        catch { /* trust boundary */ }
    }
}
