using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using UnityEngine;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// IGameMenuState: a full-screen game menu is "open" when any of the following is true:
/// <list type="bullet">
/// <item>The Main Menu (zuiroot/UILayerMain/main_funcs_list_window_pc) is active.</item>
/// <item>The Line Selector panel (zuiroot/UILayerMain/main_line_window) is active.</item>
/// <item>Any child of zuiroot/UILayerFunc is active (inventory, map, character, gear, skills, …).</item>
/// <item>Any child of zuiroot/UILayerFuncPopup is active (dungeon enter confirm: team_enter/team_copy_popup, and similar full-screen popups).</item>
/// <item>Any child of zuiroot/UILayerDramaBottom is active (NPC talk_main, talk_dialog_window, talk_option_window, …).</item>
/// <item>Any child of zuiroot/UILayerDramaVideo is active (story cutscene video sequences).</item>
/// <item>Any child of zuiroot/UILayerDramaTop is active (story top-layer overlay).</item>
/// <item>The dungeon/world-boss queue-pop confirm (common_matching / world_boss_matching) is active under zuiroot/UILayerTop.</item>
/// </list>
/// The loading screen (zuiroot/UILayerSystemTip/loading_window) is intentionally NOT detected here: it is up
/// exactly while <c>IsWorldActive</c> is false, when this probe (ticked inside the gated <c>_framework.Tick</c>)
/// is frozen. The <see cref="GameUIState.Loading"/> bit is owned by the un-gated <see cref="PandaLoadingScreenProbe"/>.
/// UILayerFunc is the game's dedicated layer for full-screen functional windows — each
/// created+activated on open and gone when closed, so "any active child" is a robust,
/// menu-agnostic signal. The three Drama layers cover NPC dialogue and story cutscenes;
/// talk_* views use UILayerDramaBottom with AudioGameState=Dialogue. The loading screen
/// and match-confirm popups use targeted prefix scans (not any-child) because their
/// host layers also contain Permanent views active during normal gameplay
/// (UILayerSystemTip: tips_broadcast/sys_dialog; UILayerTop: hero_dungeon_key).
/// Confirmed by Lua vm_scripts_path.lua UI view config. See Knowledge Base/GameMenuState.md.
///
/// <para>
/// <b>Performance.</b> The naive form called <c>GameObject.Find</c> twice <i>every
/// frame</i> — each call is a full-by-name scan of the entire scene hierarchy.
/// Fix: resolve the persistent <c>zuiroot</c> transform ONCE (re-resolving only if it
/// dies on a scene change), then test menu state with cheap relative
/// <see cref="Transform.Find"/> lookups under that cached root — which, unlike
/// <c>GameObject.Find</c>, also see <i>inactive</i> objects. The whole check is
/// throttled to ~10 Hz; a HUD that hides ~100 ms after a menu opens is imperceptible,
/// and callers read the cached bool every frame regardless.
/// </para>
/// </summary>
internal sealed class PandaMenuStateProbe : IGameMenuState
{
    private const string RootName            = "zuiroot";
    private const string MainLayerName       = "UILayerMain";
    private const string MainMenuRelPath     = "UILayerMain/main_funcs_list_window_pc(Clone)";
    private const string LineWindowPrefix    = "main_line_window";     // line selector panel (SwitchLine)
    private const string TopLayerName        = "UILayerTop";
    private const string MatchConfirmPrefix  = "common_matching";      // dungeon queue-pop confirm; IsFullScreen=true
    private const string BossMatchPrefix     = "world_boss_matching";  // world-boss queue confirm
    private const string FuncLayerName       = "UILayerFunc";
    private const string FuncPopupLayerName  = "UILayerFuncPopup";     // full-screen popups: team_enter (team_copy_popup), …
    private const string DramaBottomLayerName = "UILayerDramaBottom";  // NPC dialogue
    private const string DramaVideoLayerName  = "UILayerDramaVideo";   // story cutscene video
    private const string DramaTopLayerName    = "UILayerDramaTop";     // story top overlay
    private const string GameHudPrefix        = "main_main_pc";        // permanent gameplay HUD under UILayerMain

    // ~10 Hz at 60 fps. Menu open/close detection does not need per-frame latency.
    private const int CheckIntervalTicks = 6;

    private GameUIState _state;
    private int _ticksUntilCheck;
    private Transform? _zuiroot;   // cached persistent UI root; Unity '== null' detects scene-change destruction

    /// <summary>Legacy collapsed signal — any covering menu / full-screen surface is open.</summary>
    public bool IsFullScreenMenuOpen => (_state & GameUIState.GameHudHidden) != 0
        || (_state & (GameUIState.MainMenu | GameUIState.Dialogue | GameUIState.Matchmaking)) != 0;

    /// <summary>Un-collapsed per-layer UI state as flags (fed to ClientStateService.UiState).</summary>
    public GameUIState UiState => _state;

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
            if (_zuiroot == null) { _state = GameUIState.None; return; }
        }

        _state = Detect(_zuiroot);
    }

    // Un-collapse each detected layer into its own flag bit. Provisional cover-vs-overlay membership lives in
    // GameUIState's preset masks (see Knowledge Base/GameMenuState.md; verify in-game per design §7).
    private static GameUIState Detect(Transform root)
    {
        var s = GameUIState.None;
        if (PrefixChildActive(root, MainLayerName, GameHudPrefix)) s |= GameUIState.GameHud;
        if (NamedWindowActive(root, MainMenuRelPath))              s |= GameUIState.MainMenu;
        if (PrefixChildActive(root, MainLayerName, LineWindowPrefix)) s |= GameUIState.LineSelector;
        // NOTE: GameUIState.Loading is NOT set here — it is owned by the un-gated PandaLoadingScreenProbe
        // (this menu-state probe is frozen during a load, when IsWorldActive is false). See that probe.
        if (MatchConfirmActive(root))                             s |= GameUIState.Matchmaking;
        if (AnyChildActive(root, FuncLayerName) || AnyChildActive(root, FuncPopupLayerName))
            s |= GameUIState.FullScreenMenu;
        if (AnyChildActive(root, DramaBottomLayerName))           s |= GameUIState.Dialogue;
        if (AnyChildActive(root, DramaVideoLayerName) || AnyChildActive(root, DramaTopLayerName))
            s |= GameUIState.Cutscene;
        return s;
    }

    // Transform.Find walks the relative path only (cheap) and sees inactive objects —
    // no global scan, no menu-closed miss.
    private static bool NamedWindowActive(Transform root, string relPath)
    {
        var t = root.Find(relPath);
        return t != null && t.gameObject.activeInHierarchy;
    }

    // common_matching and world_boss_matching: Lua-configured on UILayerTop, IsFullScreen=true.
    private static bool MatchConfirmActive(Transform root)
        => PrefixChildActive(root, TopLayerName, MatchConfirmPrefix)
        || PrefixChildActive(root, TopLayerName, BossMatchPrefix);

    // Scan children of the named layer for an active one whose name starts with prefix.
    // Handles both bare names and Unity's "(Clone)" suffix without two separate lookups.
    private static bool PrefixChildActive(Transform root, string layerName, string prefix)
    {
        var layer = root.Find(layerName);
        if (layer == null) return false;
        for (var i = 0; i < layer.childCount; i++)
        {
            var child = layer.GetChild(i);
            if (child.gameObject.activeInHierarchy && child.name.StartsWith(prefix))
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
