using UnityEngine;

namespace Stellar.Infrastructure.Hooks;

/// <summary>Logs the active children of zuiroot/UILayerMain on demand, so Phase A
/// can identify which GameObject signals "a full-screen menu is open". Driven by
/// the existing STELLAR_NATIVEUI_RECON path; call DumpActiveLayers() repeatedly
/// (open/close menus between calls) to see what toggles.</summary>
internal static class MenuStateRecon
{
    public static void DumpActiveLayers(System.Action<string> log)
    {
        var root = GameObject.Find("zuiroot/UILayerMain");
        if (root == null) { log("[MenuRecon] UILayerMain not found"); return; }
        var t = root.transform;
        for (var i = 0; i < t.childCount; i++)
        {
            var c = t.GetChild(i);
            log($"[MenuRecon] child='{c.name}' active={c.gameObject.activeInHierarchy}");
        }
    }
}
