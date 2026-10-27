using System;
using HarmonyLib;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Keeps the IME usable while a Stellar text field is focused, so the Korean 한/영 key (VK_HANGUL) toggles Hangul input
/// in our fields as it does in game chat.
/// <para><b>Why:</b> <c>ZMouseManager.Update</c> → <c>updateMouseLock()</c> (RVA 0x4DDDEE0) runs every frame. When no
/// game view has registered a show-mouse source it calls <c>Panda.ZInput.DefaultMousePointer.HideMousePoint()</c>
/// (RVA 0x4DB7760), whose whole body is <c>Cursor.lockState = Locked; Cursor.visible = false;
/// Input.imeCompositionMode = Off;</c>. That Off write lands every frame, so the IME never gets a stable On window and
/// 한/영 does nothing (re-asserting On each frame just thrashes Off/On — tried, failed). The
/// <c>Input.set_imeCompositionMode</c> icall is inlined into its callers, so the setter itself can't be patched.</para>
/// <para><b>Fix:</b> a PREFIX on <c>HideMousePoint</c>. While a Stellar field is focused it reproduces the body minus
/// the IME line (lock + hide cursor, exactly as the game would) and skips the original. Stock <c>TMP_InputField</c>
/// already sets IME On on activate and Auto on deactivate, so with the Off write gone nothing else is needed — no
/// postfix re-setting On. <c>HideMousePoint</c> is reached via interface dispatch (<c>IPlayerMousePointer</c> slot →
/// <c>PlayerMousePointerAdpter.Hide</c> → tail-jmp), not inlined, so the detour catches every call. The gamepad pointer
/// variants never write IME and are left alone.</para>
/// <para><b>Rejected:</b> game chat avoids this with <c>Z.UIMgr:AddShowMouseView("main_chat_pc")</c>; doing the same
/// for our fields switches the game's InputMode to UI and shows the cursor — a visible behaviour change.</para>
/// <para>The focus signal is the one <see cref="KeyboardInputGate"/> is driven by (<c>WindowService.AnyFieldFocused</c>),
/// passed in by the Host — no second focus tracker. Installed once; the framework <see cref="IHarmonyHost"/> unpatches
/// on teardown.</para>
/// </summary>
internal static class ImeKeepOnPatch
{
    private const string TypeName = "Panda.ZInput.DefaultMousePointer";

    private static Action<string>? _log;
    private static Func<bool>? _fieldFocused;
    // One Info line per focus session: set on the first skipped call, cleared once a call sees no field focused.
    private static bool _loggedThisSession;
    private static bool _throwLogged, _installed;

    public static void Install(IHarmonyHost host, Func<bool> fieldFocused, Action<string> log)
    {
        if (_installed) return;   // one detour per process, whatever re-runs the lifecycle wiring
        _installed = true;
        _log = log;
        _fieldFocused = fieldFocused;
        try
        {
            var type = StellarInterop.FindType(TypeName);
            if (type is null) { log($"[TextInput] IME: {TypeName} not found; 한/영 stays blocked in Stellar fields"); return; }
            var m = StellarInterop.FindMethod(type, "HideMousePoint", 0);
            if (m is null) { log($"[TextInput] IME: {TypeName}.HideMousePoint() not found; not patched"); return; }
            host.Create("imekeepon").Patch(m, prefix: new HarmonyMethod(typeof(ImeKeepOnPatch), nameof(Prefix)));
            log($"[TextInput] IME: {TypeName}.HideMousePoint patched — IME stays on while a Stellar field is focused");
        }
        catch (Exception ex) { log($"[TextInput] IME: HideMousePoint patch failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // false = skip the original. Same cursor effect as the game's body; only the imeCompositionMode=Off write is dropped.
    private static bool Prefix()
    {
        try
        {
            if (_fieldFocused is null || !_fieldFocused())
            {
                _loggedThisSession = false;
                return true;
            }
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            if (!_loggedThisSession)
            {
                _loggedThisSession = true;
                _log?.Invoke("[TextInput] IME: kept on while a Stellar field is focused (game HideMousePoint skipped)");
            }
            return false;
        }
        catch (Exception ex)
        {
            // Fail open: the game's own body runs as before.
            if (!_throwLogged) { _throwLogged = true; _log?.Invoke($"[TextInput] IME: prefix threw (once): {ex.Message}"); }
            return true;
        }
    }
}
