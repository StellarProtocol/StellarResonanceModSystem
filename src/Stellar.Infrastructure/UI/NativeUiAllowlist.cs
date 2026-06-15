namespace Stellar.Infrastructure.UI;

/// <summary>
/// One entry in the hand-curated allowlist of Panda.Hud GameObjects the
/// framework is willing to move / hide. Keeping this list short + auditable
/// is the user-visible distinction between QoL (Dalamud-style) and
/// reverse-engineering: we explicitly do NOT walk arbitrary game objects.
/// </summary>
internal sealed record NativeUiAllowlistEntry(
    string Id,
    string DisplayName,
    string Path)
{
    /// <summary>
    /// When false the user cannot hide the element (still movable). Use for
    /// HUD pieces whose absence triggers game-side state machine confusion
    /// (Quickbar input grab, Player HP/MP HUD lifecycle).
    /// </summary>
    public bool SafeToHide { get; init; } = true;

    /// <summary>
    /// Optional sub-path (relative to <see cref="Path"/>, '/'-separated, passed to
    /// <c>Transform.Find</c>) of the descendant whose OWN screen-rect is used as the edit-mode outline /
    /// grab-box. Curated per element because the auto-computed bounds over-/under-shoot for the game's
    /// container HUDs (a full-screen/zero-size root, or a designed footprint with reserved empty space).
    /// Null → fall back to the auto rect (usable node rect, else visible-drawable union). The element is
    /// still MOVED by translating <see cref="Path"/>; only the outline rect comes from this child.
    /// </summary>
    public string? RectChild { get; init; }
}

internal static class NativeUiAllowlist
{
    // Real Panda.Hud hierarchy paths, captured via the Game UI panel's
    // [Recon paths…] walk on build 2.11 (2026-06-01). PandaHudAdapter.TryResolve
    // does GameObject.Find(fullPath) first, then a Resources.FindObjectsOfTypeAll
    // <Canvas> + transform.Find fallback. GameObject.Find only matches *active*
    // objects, so combat-HUD entries resolve once in-world (and the battle HUD
    // is up); they read "(not present)" on the title/character screens.
    //
    // The always-on HUD lives under main_main_pc(Clone); the HP/MP, EXP, skill
    // bar and buff icons live under the combat window battle_main_node_window_pc.
    private const string MainHud =
        "zuiroot/UILayerMain/main_main_pc(Clone)/anim/node_main";
    private const string BattleHud =
        MainHud + "/node_fight_canvas_node/group_lower_left/anim_fighter/node_fighter"
                + "/battle_main_node_window_pc(Clone)/anim_battle";

    // The chat window is a top-level UILayerMain canvas (sibling of main_main_pc),
    // captured 2026-06-01 from ChatProbe's own logged hierarchy paths.
    private const string ChatWindow = "zuiroot/UILayerMain/main_chat_pc(Clone)";

    // Target Frame = the boss HP bar (top-centre), a top-level UILayerMain canvas
    // that only instantiates during a boss fight, captured 2026-06-01 facing the
    // Brigand Leader. Ordinary mobs have no screen-space frame (world-space
    // nameplate, out of v1 scope), so this reads "(not present)" outside boss
    // encounters — expected.
    private const string TargetFrame = "zuiroot/UILayerMain/battle_boss_blood_sub_pc(Clone)";

    public static readonly NativeUiAllowlistEntry[] V1Targets =
    {
        // RectChild (curated 2026-06-15 from the rect-diag full-subtree dump): tightens the edit outline to the
        // visible widget. Party = the member panel (whole panel incl. raid grid, per user). Quickbar = the bar
        // container's own rect (excludes the skill-drawer slot that floats off to the top-right). Player-HP =
        // HP bar ⊎ stamina bar contents (with their numbers), excluding the class gauge between them.
        new("gameui.party-panel",   "Party Panel",   MainHud + "/node_upper_right/anim_upper_right/node_team")
            { RectChild = "group_team/main_team_sub_pc(Clone)/node_member" },
        new("gameui.minimap",       "Minimap",       MainHud + "/anim_upper_left/node_upper_left/group_minimap"),
        new("gameui.quickbar",      "Quickbar",      BattleHud + "/button_pos_group_hide_root")     { SafeToHide = false,
            RectChild = "button_pos_group" },
        new("gameui.chat-window",   "Chat Window",   ChatWindow),
        new("gameui.target-frame",  "Target Frame",  TargetFrame),
        new("gameui.buff-bar",      "Buff Bar",      BattleHud + "/profession_buff_icon_group"),
        new("gameui.player-hp",     "Player HP/MP",  BattleHud + "/node_player_state_bar_hide_root") { SafeToHide = false,
            RectChild = "*node_player_state_bar/group_info_layout/group_blood;*node_player_state_bar/group_info_layout/group_str" },
        // The class/resonance gauge (the "100/100 ←→" energy bar) — a separate movable component. It's the
        // group_energy node, a sibling of HP/stamina under group_info_layout, so it translates independently of
        // (and additionally to) the Player HP/MP bar. Content union so the wide effect/arrows are framed.
        new("gameui.class-gauge",   "Class Gauge",   BattleHud + "/node_player_state_bar_hide_root/node_player_state_bar/group_info_layout/group_energy")
            { RectChild = "*." },
        // Player Profile = the bottom-left identity bar: level (node_lv_max), name (lab_name), UID (lab_uid),
        // title, talent emblem, AND the XP bar (node_experience is a child). Merges the old standalone EXP-bar
        // element into the whole identity component, per user. Content union so the level (anchored left of the
        // node origin) and all labels are framed.
        new("gameui.player-profile", "Player Profile", BattleHud + "/node_protection") { RectChild = "*." },
        // Whole body at the title's width: the scroll list is a fixed 534px and the text doesn't fill it, so
        // width comes from the title bar (330px) and HEIGHT extends down to the scroll list ("h:" = vertical-only).
        new("gameui.quest-tracker", "Quest Tracker", MainHud + "/anim_upper_left/node_upper_left/node_left_track")
            { RectChild = "node_track_title;h:node_track_sub/track_bar_sub_pc(Clone)/node_parent/scrollview" },
    };
}
