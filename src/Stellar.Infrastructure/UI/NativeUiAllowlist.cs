using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.UI;

/// <summary>
/// One entry in the hand-curated allowlist of Panda.Hud GameObjects the
/// framework is willing to move / hide. Keeping this list short + auditable
/// is the user-visible distinction between QoL (Dalamud-style) and
/// reverse-engineering: we explicitly do NOT walk arbitrary game objects.
/// </summary>
/// <param name="DisplayNameKey">A framework localization key (<c>gameui.entry.*</c>), NOT display text —
/// resolved via <see cref="Stellar.Abstractions.Services.ILocalization.T"/> at render time by
/// <c>GameUiPanel</c> and the layout-editor outline label, so the Settings → Game UI list and the
/// Shift+` edit-mode outline follow the active UI language.</param>
internal sealed record NativeUiAllowlistEntry(
    string Id,
    string DisplayNameKey,
    string Path)
{
    /// <summary>
    /// When false the user cannot hide the element (still movable). Every current entry is hideable: the hide is a
    /// scale-0 on OUR wrapper, which keeps the element active (verified safe 2026-11-11 for Quickbar + Player HP/MP —
    /// skills by key still fire, bars keep updating). The old false flags were an untested pre-recon precaution
    /// against SetActive(false), which is no longer used. Kept for a future element that genuinely needs it.
    /// </summary>
    public bool SafeToHide { get; init; } = true;

    /// <summary>
    /// Optional sub-path (relative to <see cref="Path"/>, '/'-separated, passed to
    /// <c>Transform.Find</c>) of the descendant whose OWN screen-rect is used as the edit-mode outline /
    /// grab-box. Curated per element because the auto-computed bounds over-/under-shoot for the game's
    /// container HUDs (a full-screen/zero-size root, or a designed footprint with reserved empty space).
    /// Null → fall back to the auto rect (usable node rect, else visible-drawable union). The element is
    /// still MOVED through its wrapper; only the outline rect comes from this child.
    /// </summary>
    public string? RectChild { get; init; }

    /// <summary>Where our wrapper goes (default <see cref="NativeUiWrapMode.Outer"/> = becomes the parent). Inner for
    /// view roots the game re-parents on every show (chat, boss bar) and for a node under a LayoutGroup (class gauge).</summary>
    public NativeUiWrapMode Wrap { get; init; } = NativeUiWrapMode.Outer;

    /// <summary>While hidden, also block the element's input (CanvasGroup on our wrapper) — chat only.</summary>
    public bool BlockInputWhenHidden { get; init; }

    /// <summary>Edit-mode placeholder before the element was ever seen: screen fractions (x, y, w, h; top-left / y-down).</summary>
    public WindowRect DefaultFraction { get; init; }

    /// <summary>Placeholder hint key (null → generic "Hidden by the game now").</summary>
    public string? HintKey { get; init; }

    /// <summary>Optional placeholder sub line key.</summary>
    public string? SubHintKey { get; init; }
}

internal static class NativeUiAllowlist
{
    // Real Panda.Hud hierarchy paths, captured via the Game UI panel's [Recon paths…] walk on build 2.11
    // (2026-06-01). PandaHudAdapter.TryResolve walks the FULL path segment by segment from "zuiroot" (every path
    // must start there) with Transform.Find, which also finds INACTIVE nodes — so a hidden element resolves too and
    // no Resources.FindObjectsOfTypeAll scan is needed. Combat-HUD entries resolve once in-world (battle HUD built).
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
    // encounters — expected (edit mode draws a placeholder for it).
    private const string TargetFrame = "zuiroot/UILayerMain/battle_boss_blood_sub_pc(Clone)";

    // DefaultFraction = a rough placement of each element in the game's default layout. Only used to draw an
    // edit-mode placeholder before the element was ever seen at this resolution (then its last-seen rect is used).
    public static readonly NativeUiAllowlistEntry[] V1Targets =
    {
        // RectChild (curated 2026-06-15 from the rect-diag full-subtree dump): tightens the edit outline to the
        // visible widget. Party = the member panel (whole panel incl. raid grid, per user). Quickbar = the bar
        // container's own rect (excludes the skill-drawer slot that floats off to the top-right). Player-HP =
        // HP bar ⊎ stamina bar contents (with their numbers), excluding the class gauge between them.
        new("gameui.party-panel",   "gameui.entry.partyPanel",   MainHud + "/node_upper_right/anim_upper_right/node_team")
            { RectChild = "group_team/main_team_sub_pc(Clone)/node_member",
              DefaultFraction = new(0.78f, 0.14f, 0.20f, 0.34f), SubHintKey = "gameui.ph.partySub" },
        new("gameui.minimap",       "gameui.entry.minimap",      MainHud + "/anim_upper_left/node_upper_left/group_minimap")
            { DefaultFraction = new(0.01f, 0.02f, 0.11f, 0.20f) },
        new("gameui.quickbar",      "gameui.entry.quickbar",     BattleHud + "/button_pos_group_hide_root")
            { RectChild = "button_pos_group", DefaultFraction = new(0.32f, 0.86f, 0.36f, 0.08f) },
        // View root: the game detaches + re-parents it on every full-screen menu → INNER wrapper (verified over 3
        // menu cycles). While hidden it is also input-blocked so Enter can't focus the invisible box.
        new("gameui.chat-window",   "gameui.entry.chatWindow",   ChatWindow)
            { Wrap = NativeUiWrapMode.Inner, BlockInputWhenHidden = true, DefaultFraction = new(0.01f, 0.62f, 0.25f, 0.25f) },
        // View root too (instantiated per boss fight) → INNER wrapper.
        new("gameui.target-frame",  "gameui.entry.targetFrame",  TargetFrame)
            { Wrap = NativeUiWrapMode.Inner, DefaultFraction = new(0.30f, 0.11f, 0.40f, 0.09f), HintKey = "gameui.ph.bossFights" },
        new("gameui.buff-bar",      "gameui.entry.buffBar",      BattleHud + "/profession_buff_icon_group")
            { DefaultFraction = new(0.40f, 0.76f, 0.20f, 0.05f) },
        new("gameui.player-hp",     "gameui.entry.playerHp",     BattleHud + "/node_player_state_bar_hide_root")
            { RectChild = "*node_player_state_bar/group_info_layout/group_blood;*node_player_state_bar/group_info_layout/group_str",
              DefaultFraction = new(0.33f, 0.81f, 0.34f, 0.04f) },
        // The class/resonance gauge (the "100/100 ←→" energy bar) — a separate movable component. It's the
        // group_energy node, a sibling of HP/stamina under group_info_layout, so it moves independently of
        // (and additionally to) the Player HP/MP bar. Content union so the wide effect/arrows are framed. INNER
        // wrapper: its parent group_info_layout is likely a LayoutGroup (an outer wrapper would be laid out by it).
        new("gameui.class-gauge",   "gameui.entry.classGauge",   BattleHud + "/node_player_state_bar_hide_root/node_player_state_bar/group_info_layout/group_energy")
            { RectChild = "*.", Wrap = NativeUiWrapMode.Inner, DefaultFraction = new(0.42f, 0.77f, 0.16f, 0.03f) },
        // Player Profile = the bottom-left identity bar: level (node_lv_max), name (lab_name), UID (lab_uid),
        // title, talent emblem, AND the XP bar (node_experience is a child). Merges the old standalone EXP-bar
        // element into the whole identity component, per user. Content union so the level (anchored left of the
        // node origin) and all labels are framed.
        new("gameui.player-profile", "gameui.entry.playerProfile", BattleHud + "/node_protection")
            { RectChild = "*.", DefaultFraction = new(0.01f, 0.93f, 0.28f, 0.06f) },
        // Whole body at the title's width: the scroll list is a fixed 534px and the text doesn't fill it, so
        // width comes from the title bar (330px) and HEIGHT extends down to the scroll list ("h:" = vertical-only).
        new("gameui.quest-tracker", "gameui.entry.questTracker", MainHud + "/anim_upper_left/node_upper_left/node_left_track")
            { RectChild = "node_track_title;h:node_track_sub/track_bar_sub_pc(Clone)/node_parent/scrollview",
              DefaultFraction = new(0.01f, 0.40f, 0.18f, 0.22f) },
    };
}
