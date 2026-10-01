using System;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// The game's quality-apply entry points (recon § Quality-apply entry points hooked). Each postfix only raises
/// <see cref="ZRenderQualityBackend.GameApplied"/>; the host turns that into a re-assert on the next framework tick
/// (so the game's own apply finishes first). No game call runs inside a postfix. Each hook installs independently
/// and fails open with one log line.
/// </summary>
internal sealed partial class ZRenderQualityBackend
{
    private const string QualityWrapType = "Panda_Utility_Quality_QualityGradeSettingWrap";
    private const string FlowActionType = "DreamMaker.Logic.EPFlowActionQualityGradeSetting";
    private const string ApplyAllData = "ApplyAllData";   // ref QualityData parameter — the kill-switchable hook

    // QualityGradeSetting statics. applyEnableAA is absent on purpose: inlined (CallerCount 0), and we call it ourselves.
    // ClearAAHistory is absent on purpose: 12 native callers (camera cuts) — too hot for an allocating postfix.
    private static readonly string[] QualityStatics =
    {
        ApplyAllData, "applyRenderScale", "applyShadowGrade", "ApplyResolution", "ResetResolution",
        "PostCheckQualityGrade", "set_QualityGrade", "set_UseExtendRenderScale", "Init",
        "set_RenderScale", "set_EnableAA", "set_ShadowGrade", "SetExtendScaleWithoutSave",
    };

    // The settings panel's tolua entry points (int fn(IntPtr L)) — cover a setter inlined into its wrap.
    private static readonly string[] QualityWrapStatics =
    {
        "set_QualityGrade", "set_RenderScale", "set_EnableAA", "set_ShadowGrade", "set_ExtendScale",
        "set_UseExtendRenderScale", "ResetResolution", "SetExtendScaleWithoutSave", "PostCheckQualityGrade",
    };

    private readonly LazyHookInstall _hooks = new();

    /// <summary>
    /// Makes the game-apply postfixes installable (call once, after the hot-update assemblies load). They install on
    /// the first <c>Request</c> (<see cref="EnsureHooks"/>), never at boot. <paramref name="skipApplyAllData"/>
    /// (env <c>STELLAR_RQ_NO_APPLYALLDATA=1</c>) leaves out the one hook with a by-ref struct parameter.
    /// </summary>
    public void ArmHooks(HarmonyGameMethodHooker hooker, bool skipApplyAllData) =>
        _hooks.Arm(() => InstallHooks(hooker, skipApplyAllData));

    public void EnsureHooks() => _hooks.Request();

    private void InstallHooks(HarmonyGameMethodHooker hooker, bool skipApplyAllData)
    {
        var statics = skipApplyAllData ? Array.FindAll(QualityStatics, m => m != ApplyAllData) : QualityStatics;
        if (skipApplyAllData) _log.Info(Tag + "ApplyAllData re-assert hook skipped (STELLAR_RQ_NO_APPLYALLDATA=1).");
        HookAll(hooker, QualityGradeType, statics, isStatic: true);
        HookAll(hooker, QualityWrapType, QualityWrapStatics, isStatic: true);
        HookAll(hooker, FlowActionType, new[] { "OnEnter" }, isStatic: false);
        HookAll(hooker, ShadowPassType, new[] { "OnInitialize" }, isStatic: false);
    }

    private void HookAll(HarmonyGameMethodHooker hooker, string typeName, string[] methods, bool isStatic)
    {
        Type? t;
        try { t = _types.FindType(typeName); }
        catch { t = null; }
        if (t is null)
        {
            WarnOnce("hooktype:" + typeName, $"re-assert hooks on {typeName} unavailable (type not found); scene changes still re-assert.");
            return;
        }
        foreach (var method in methods)
        {
            var name = method;   // captured per hook for the diagnostics line
            try
            {
                if (isStatic) hooker.PostfixStaticOverloads(t, name, (_, _) => OnGameApply(typeName + "." + name));
                else hooker.PostfixAllOverloads(t, name, (_, _) => OnGameApply(typeName + "." + name));
            }
            catch (Exception ex)
            {
                WarnOnce("hook:" + typeName + "." + name, $"re-assert hook {typeName}.{name} failed: {ex.Message}");
            }
        }
    }
}
