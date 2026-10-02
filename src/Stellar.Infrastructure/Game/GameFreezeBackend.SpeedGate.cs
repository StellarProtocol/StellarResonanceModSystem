using System;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The <c>AnimCompBase.set_Speed</c> gate (owner report 2026-10-02: monsters resume a few seconds into a frozen
/// fight — the game rewrites their drawn speed; recon run 7). Installed with the freeze's other hooks on the first freeze;
/// armed per freeze in <c>FreezeAll</c>, disarmed first thing in <c>UnfreezeAll</c>. Components are tracked by native
/// pointer at stage 2 and while an appearing entity is watched — the prefix never reflects.</summary>
internal sealed partial class GameFreezeBackend
{
    private bool _speedGateInstalled;

    private void InstallSpeedGate(HarmonyGameMethodHooker hooker)
    {
        var comp = _types.FindType(DrawnSpeedPatch.AnimCompType);
        if (comp is null) { WarnOnce("speedgate", "monsters may resume in combat while frozen (AnimCompBase not found)"); return; }
        try { _speedGateInstalled = DrawnSpeedPatch.Install(hooker, comp, _speedGate); }
        catch (Exception ex) { WarnOnce("speedgate", "drawn-speed gate failed: " + ex.Message); }
        if (!_speedGateInstalled) WarnOnce("speedgate", "monsters may resume in combat while frozen (set_Speed not patched)");
        InstallEcsGate(hooker);   // .Ecs.cs: the ECS animator's own layer-speed writers
    }

    private static IntPtr CompPointer(object comp)
    {
        try { return (comp as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero; }
        catch { return IntPtr.Zero; }
    }
}
