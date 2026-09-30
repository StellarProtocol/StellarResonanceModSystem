using System;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>
/// The game's own photo / selfie / cutscene state, from postfix hooks (recon rows PHOTO_HOOKS + CUTSCENE_SIGNAL in
/// docs/recon/photo-studio-render-recon.md). Each hook RESOLVES the state rather than counting edges — the game's
/// selfie→camera toggle bounces SelfPhoto enter/exit — and raises the derived kind; <c>PhotoModeService</c>
/// dedupes. Constructed at boot (before plugin services exist); <see cref="Install"/> runs once the hot-update
/// assemblies (Panda.Script) are loaded. Every hook installs independently and fails open with one warning.
/// The hooked game methods run on the Unity main thread, so events are raised there.
/// </summary>
internal sealed class PandaPhotoModeProbe : IPhotoModeProbe
{
    private const string Tag = "[PhotoStudio] ";
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const string CameraFrameCtrlType = "Panda.ZGame.CameraFrameCtrl";
    private const string SelfPhotoStateType = "Panda.ZGame.CameraStateSelfPhoto";
    private const string CutsceneManagerType = "Panda.ZGame.Timeline.CutsceneManager";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private bool _inCamera;
    private bool _selfie;

    public PandaPhotoModeProbe(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types;
        _log = log;
    }

    public event Action<PhotoModeKind>? KindChanged;
    public event Action<bool>? CutsceneChanged;

    /// <summary>Installs the hooks and seeds the cutscene state. Call once, after the hot-update assemblies load.</summary>
    public void Install(HarmonyGameMethodHooker hooker)
    {
        try
        {
            Hook(hooker, CameraFrameCtrlType, "RecordCameraInitialParameters", (_, _) => { _inCamera = true; RaiseKind(); });
            Hook(hooker, CameraFrameCtrlType, "ResetCameraInitialParameters", (_, _) => { _inCamera = false; RaiseKind(); });
            Hook(hooker, SelfPhotoStateType, "OnEnter", (_, _) => { _selfie = true; RaiseKind(); });
            Hook(hooker, SelfPhotoStateType, "OnExit", (_, _) => { _selfie = false; RaiseKind(); });
            Hook(hooker, CutsceneManagerType, "Play", (_, _) => CutsceneChanged?.Invoke(true));
            Hook(hooker, CutsceneManagerType, "afterStop", (_, _) => CutsceneChanged?.Invoke(false));
            SeedCutscene();
        }
        catch (Exception ex)
        {
            _log.Warning(Tag + $"photo-mode signals disabled: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RaiseKind() =>
        KindChanged?.Invoke(_selfie ? PhotoModeKind.Selfie : _inCamera ? PhotoModeKind.CameraFrame : PhotoModeKind.None);

    private void Hook(HarmonyGameMethodHooker hooker, string typeName, string method, Action<object?, object?[]> callback)
    {
        var t = _types.FindType(typeName);
        if (t is null || !t.GetMethods(AnyInstance).Any(m => m.Name == method))
        {
            _log.Warning(Tag + $"photo-mode signal {typeName}.{method} unavailable (not found).");
            return;
        }
        hooker.PostfixAllOverloads(t, method, callback);
    }

    // CutsceneManager.Instance.InCutscene, read once — only when the singleton already exists (never construct it).
    private void SeedCutscene()
    {
        var t = _types.FindType(CutsceneManagerType);
        if (t is null) return;
        var isCreated = StellarInterop.FindPropertyUp(t, "IsCreated")?.GetGetMethod(nonPublic: true);
        if (isCreated is not { IsStatic: true } || isCreated.Invoke(null, null) is not true) return;
        var mgr = StellarInterop.GetSingleton(t);
        if (mgr is not null && StellarInterop.FindPropertyUp(t, "InCutscene")?.GetValue(mgr) is true) CutsceneChanged?.Invoke(true);
    }
}
