using System;
using HarmonyLib;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Stops a mouse press on a Stellar window from reaching the game's PRESS-CONTAINMENT checks underneath it.
/// <para>Normal game UI is already blocked by our canvas (uGUI hands a click only to the top-most raycast hit). But
/// <c>Panda.ZUi.ZUIPointPressCheck</c> (dump.cs:144499, Lua <c>uiBinder.press_check</c>) polls in its own
/// <c>Update</c>: on a mouse-button-down it calls <c>isPointerOverGameObject(Vector2)</c>, which runs
/// <c>EventSystem.RaycastAll</c> and asks whether ANY hit (not just the top one) is one of its registered objects,
/// then fires <c>ContainGoEvent(bool)</c>. So a press on a Stellar window drawn over the game chat area still counted
/// as "inside the chat": <c>main_chat_pc</c> ran <c>changeActiveState(true)</c> → <c>ActivateInputField()</c>, and
/// the next keys were typed into the hidden native chat input. ~90 game views use this component (chat, tips,
/// popups that close on an outside press).</para>
/// <para>The fix is a PREFIX on <c>isPointerOverGameObject(Vector2)</c> (RVA 0x4D1B600; Vector2 by value, no in/ref
/// struct). When the top-most raycast hit at the press point is a Stellar canvas, it returns false without running
/// the original: the game sees an OUTSIDE press, exactly as for a click on any other UI. Verified from the
/// disassembly that <c>Update</c> really calls it (call at 0x184D1B573, result → <c>containGoEvent_.Invoke</c>), so
/// the detour is not bypassed by inlining. Top-most-hit (the same test as <c>IInputShield.IsPointerOverGameUi</c>)
/// rather than window-rect containment because it matches what uGUI already blocks: a <c>Passive</c> window (no
/// raycast target) stays click-through, and z-order / popups / the layout editor's blocker are handled for free.</para>
/// <para>Called only on press frames (the game gates on <c>GetMouseButtonDown</c> first), so the extra raycast costs
/// nothing per frame. Installed once; the framework <see cref="IHarmonyHost"/> unpatches on teardown.</para>
/// </summary>
internal static class GamePressCheckShield
{
    private const string TypeName = "Panda.ZUi.ZUIPointPressCheck";
    private const float LogIntervalSec = 5f;

    private static Action<string>? _log;
    private static readonly Il2CppSystem.Collections.Generic.List<RaycastResult> _hits = new();
    // Several press-check views are live at once and each calls the target on the same press: one raycast per press.
    private static int _cacheFrame = -1;
    private static Vector2 _cachePos;
    private static string? _cacheWindow;
    private static float _lastLogTime = -LogIntervalSec;
    private static int _suppressedSinceLog;
    private static int _lastCountedFrame = -1;
    private static bool _throwLogged, _installed;

    public static void Install(IHarmonyHost host, Action<string> log)
    {
        if (_installed) return;   // one detour per process, whatever re-runs the lifecycle wiring
        _installed = true;
        _log = log;
        try
        {
            var type = StellarInterop.FindType(TypeName);
            if (type is null) { log($"[PressShield] {TypeName} not found; game press-checks not shielded"); return; }
            var m = StellarInterop.FindMethod(type, "isPointerOverGameObject", 1);
            if (m is null || m.ReturnType != typeof(bool))
            {
                log($"[PressShield] {TypeName}.isPointerOverGameObject(Vector2) not found; not shielded");
                return;
            }
            host.Create("pressshield").Patch(m, prefix: new HarmonyMethod(typeof(GamePressCheckShield), nameof(Prefix)));
            log($"[PressShield] {TypeName}.isPointerOverGameObject patched — presses on Stellar windows no longer reach game press-checks");
        }
        catch (Exception ex) { log($"[PressShield] patch failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    // false = skip the original: report "pointer not over your objects" so the game treats it as an outside press.
    private static bool Prefix(ref bool __result, Vector2 __0)
    {
        try
        {
            var window = StellarWindowAt(__0);
            if (window is null) return true;
            __result = false;
            NoteSuppressed(window);
            return false;
        }
        catch (Exception ex)
        {
            // Fail open: the game's own check runs as before.
            if (!_throwLogged) { _throwLogged = true; _log?.Invoke($"[PressShield] prefix threw (once): {ex.Message}"); }
            return true;
        }
    }

    // Name of the Stellar window under the pointer when the TOP-MOST raycast hit is a Stellar canvas, else null.
    private static string? StellarWindowAt(Vector2 pos)
    {
        var frame = Time.frameCount;
        if (frame == _cacheFrame && pos == _cachePos) return _cacheWindow;
        _cacheFrame = frame;
        _cachePos = pos;
        _cacheWindow = null;
        var es = EventSystem.current;
        if (es == null) return null;
        _hits.Clear();
        es.RaycastAll(new PointerEventData(es) { position = pos }, _hits);
        if (_hits.Count == 0 || UnityShieldInputReader.IsGameSortingOrder(_hits[0].sortingOrder)) return null;
        var go = _hits[0].gameObject;
        _cacheWindow = go != null ? WindowName(go.transform) : "(stellar)";
        return _cacheWindow;
    }

    // Window roots are direct children of a Stellar canvas, itself a scene root (DontDestroyOnLoad); the framework
    // names each root after spec.Id, so the child-of-a-root ancestor is the window id.
    private static string WindowName(Transform t)
    {
        while (t.parent != null && t.parent.parent != null) t = t.parent;
        return t.name;
    }

    // One line per press at most, and at most one per LogIntervalSec; skipped presses are counted into the next line.
    private static void NoteSuppressed(string window)
    {
        var frame = Time.frameCount;
        if (frame == _lastCountedFrame) return;   // the same press seen by another press-check view
        _lastCountedFrame = frame;
        _suppressedSinceLog++;
        var now = Time.realtimeSinceStartup;
        if (now - _lastLogTime < LogIntervalSec) return;
        _lastLogTime = now;
        var extra = _suppressedSinceLog > 1 ? $" (+{_suppressedSinceLog - 1} since last line)" : "";
        _suppressedSinceLog = 0;
        _log?.Invoke($"[PressShield] blocked game press-check under {window}{extra}");
    }
}
