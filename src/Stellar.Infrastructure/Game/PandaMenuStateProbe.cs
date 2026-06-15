using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IGameMenuState: a full-screen game menu is "open" when either the Main Menu
/// (zuiroot/UILayerMain/main_funcs_list_window_pc) is active, OR any child of
/// zuiroot/UILayerFunc is active. UILayerFunc is the game's dedicated layer for
/// full-screen functional windows (inventory bag_backpack_main_pc, map_main_pc,
/// character weapon_role_main_pc, gear/skills/talents/…), each created+activated
/// on open and gone when closed — so "any active UILayerFunc child" is a robust,
/// menu-agnostic signal that covers them all without enumerating each. Confirmed
/// by in-world recon 2026-06-02 (docs/recon/2026-06-02-menu-state.md).
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
    private const string MainMenuRelPath = "UILayerMain/main_funcs_list_window_pc(Clone)";
    private const string FuncLayerName = "UILayerFunc";

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

        _open = MainMenuOpen(_zuiroot) || AnyFuncWindowOpen(_zuiroot);
    }

    private static bool MainMenuOpen(Transform root)
    {
        // Transform.Find walks the named relative path only (cheap) and finds the
        // window even while inactive — so no global scan and no menu-closed miss.
        var t = root.Find(MainMenuRelPath);
        return t != null && t.gameObject.activeInHierarchy;
    }

    // Any active child under the functional-window layer = a full-screen menu is up.
    private static bool AnyFuncWindowOpen(Transform root)
    {
        var layer = root.Find(FuncLayerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
            if (layer.GetChild(i).gameObject.activeInHierarchy) return true;
        return false;
    }
}
