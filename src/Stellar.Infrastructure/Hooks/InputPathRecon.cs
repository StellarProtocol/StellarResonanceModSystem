using UnityEngine;
using UnityEngine.EventSystems;

namespace Stellar.Infrastructure.Hooks;

/// <summary>One-shot dump (STELLAR_INPUT_RECON=1) of the live input plumbing,
/// so Phase 0 knows whether the game's map/world click goes through the uGUI
/// EventSystem (→ gate it) or legacy Input / the InputSystem package (→ patch it).</summary>
internal static class InputPathRecon
{
    private static bool _done;
    public static void DumpOnce(System.Action<string> log)
    {
        if (_done) return;
        _done = true;
        var es = EventSystem.current;
        log($"[InputRecon] EventSystem.current={(es != null ? es.name : "<null>")} " +
            $"enabled={(es != null && es.enabled)} module={(es != null && es.currentInputModule != null ? es.currentInputModule.GetType().Name : "<none>")}");
        log($"[InputRecon] legacy Input.mousePresent={Input.mousePresent} simulateMouseWithTouches={Input.simulateMouseWithTouches}");
        var mouseType = System.Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem");
        log($"[InputRecon] new-InputSystem present={(mouseType != null)}");
    }
}
