using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IGameMenuState: a full-screen game menu is "open" when any of the following is true:
/// <list type="bullet">
/// <item>The Main Menu (zuiroot/UILayerMain/main_funcs_list_window_pc) is active.</item>
/// <item>Any child of zuiroot/UILayerFunc is active (inventory, map, character, gear, skills, …).</item>
/// <item>Any child of zuiroot/UILayerDramaBottom is active (NPC talk_main, talk_dialog_window, talk_option_window, …).</item>
/// <item>Any child of zuiroot/UILayerDramaVideo is active (story cutscene video sequences).</item>
/// <item>Any child of zuiroot/UILayerDramaTop is active (story top-layer overlay).</item>
/// <item>The loading screen (zuiroot/UILayerSystemTip/loading_window) is active.</item>
/// </list>
/// UILayerFunc is the game's dedicated layer for full-screen functional windows — each
/// created+activated on open and gone when closed, so "any active child" is a robust,
/// menu-agnostic signal. The three Drama layers cover NPC dialogue and story cutscenes;
/// talk_* views use UILayerDramaBottom with AudioGameState=Dialogue. The loading screen
/// is a targeted path check (not any-child) because UILayerSystemTip also hosts
/// tips_broadcast and sys_dialog which are active during normal gameplay. Confirmed by Lua
/// vm_scripts_path.lua UI view config. Menu-state recon: docs/recon/2026-06-02-menu-state.md.
///
/// <para>
/// <b>Performance.</b> The naive form called <c>GameObject.Find</c> twice <i>every
/// frame</i> — each call is a full-by-name scan of the entire scene hierarchy.
/// In a populated scene that measured ~3 ms/frame (the dominant cost of the whole
/// mod; see docs/superpowers/specs perf harness). Fix: resolve the persistent
/// <c>zuiroot</c> transform ONCE (re-resolving only if it dies on a scene change),
/// then test menu state with cheap relative <see cref="Transform.Find"/> lookups
/// under that cached root — which, unlike <c>GameObject.Find</c>, also see
/// <i>inactive</i> objects. The whole check is throttled to ~10 Hz; a HUD that
/// hides ~100 ms after a menu opens is imperceptible, and callers read the cached
/// bool every frame regardless.
/// </para>
/// </summary>
internal sealed class PandaMenuStateProbe : IGameMenuState
{
    private const string RootName = "zuiroot";
    private const string MainMenuRelPath      = "UILayerMain/main_funcs_list_window_pc(Clone)";
    private const string SystemTipLayerName  = "UILayerSystemTip";
    private const string LoadingWindowPrefix = "loading_window";  // matches "loading_window" and "loading_window(Clone)"
    private const string FuncLayerName       = "UILayerFunc";
    private const string DramaBottomLayerName = "UILayerDramaBottom";   // NPC dialogue (talk_main, talk_dialog_window, …)
    private const string DramaVideoLayerName  = "UILayerDramaVideo";    // story cutscene video
    private const string DramaTopLayerName    = "UILayerDramaTop";      // story top overlay

    // ~10 Hz at 60 fps. Menu open/close detection does not need per-frame latency.
    private const int CheckIntervalTicks = 6;

    private bool _open;
    private int _ticksUntilCheck;
    private Transform? _zuiroot;   // cached persistent UI root; Unity '== null' detects scene-change destruction

    public bool IsFullScreenMenuOpen => _open;

    public void Tick()
    {
        if (--_ticksUntilCheck > 0) return;
        _ticksUntilCheck = CheckIntervalTicks;

        // (Re)resolve the root only when missing/destroyed — the only global scan,
        // and it runs ~once per scene rather than twice per frame.
        if (_zuiroot == null)
        {
            var root = GameObject.Find(RootName);
            _zuiroot = root != null ? root.transform : null;
            if (_zuiroot == null) { _open = false; return; }
        }

        _open = NamedWindowActive(_zuiroot, MainMenuRelPath)
             || LoadingScreenActive(_zuiroot)
             || AnyChildActive(_zuiroot, FuncLayerName)
             || AnyChildActive(_zuiroot, DramaBottomLayerName)
             || AnyChildActive(_zuiroot, DramaVideoLayerName)
             || AnyChildActive(_zuiroot, DramaTopLayerName);
    }

    // Transform.Find walks the relative path only (cheap) and sees inactive objects —
    // no global scan, no menu-closed miss.
    private static bool NamedWindowActive(Transform root, string relPath)
    {
        var t = root.Find(relPath);
        return t != null && t.gameObject.activeInHierarchy;
    }

    // UILayerSystemTip also hosts tips_broadcast/sys_dialog (active during normal play),
    // so we can't use AnyChildActive. Scan by name prefix to match both
    // "loading_window" and "loading_window(Clone)" regardless of Instantiate suffix.
    private static bool LoadingScreenActive(Transform root)
    {
        var layer = root.Find(SystemTipLayerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
        {
            var child = layer.GetChild(i);
            if (child.gameObject.activeInHierarchy && child.name.StartsWith(LoadingWindowPrefix))
                return true;
        }
        return false;
    }

    // Any active child under the named layer = that UI surface is in use.
    private static bool AnyChildActive(Transform root, string layerName)
    {
        var layer = root.Find(layerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
            if (layer.GetChild(i).gameObject.activeInHierarchy) return true;
        return false;
    }
}
