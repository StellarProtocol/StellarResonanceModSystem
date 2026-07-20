namespace Stellar.Abstractions.Domain;

/// <summary>
/// Single source of truth for the framework's version string. Lives in
/// Abstractions so every layer (Host's BepInPlugin manifest, Infrastructure's
/// AboutPanel, and any plugin that wants to gate behaviour on a particular
/// framework release) can read the same constant without re-declaring it.
/// Bump this on each user-visible release; Host's <c>BootstrapPlugin.PluginVersion</c>
/// forwards to it so the BepInEx plugin manifest stays in lockstep.
/// </summary>
/// <remarks>
/// BepInEx parses <see cref="Value"/> with SemVer semantics — it rejects
/// trailing letters like <c>0.9.0a</c> ("Skipping type because its version
/// is invalid"). Use SemVer pre-release suffix syntax (<c>0.9.0-alpha</c>)
/// so the chainloader accepts the manifest.
/// </remarks>
public static class FrameworkVersion
{
    /// <summary>
    /// Current framework version. Plain SemVer (no pre-release suffix) keeps the
    /// BepInEx chainloader happy. 1.14.0 adds <c>EntityVitals.HasHpObservation</c> (MaxHp-only
    /// observations no longer read as dead; init-prop, binary-compatible), AOI-appear vitals
    /// seeding, <c>PartyMember.FastSyncState</c> (raw TeamMemberFastSyncData.state transport;
    /// init-prop), and <c>IDungeonState.CurrentFlowState</c>/<c>FlowStateVersion</c>
    /// (EDungeonState surfaced with a poll-friendly transition counter) — all additive,
    /// binary-compatible with plugins built against ≤1.13.0.
    /// 1.13.0 adds <c>IExchange.GetStallSubcategoryMap</c>
    /// (live StallDetailTable membership). 1.12.0 adds <c>IGameEnvironment</c> — region
    /// (SEA/JP/Unknown) + installed game version, detected once at boot from
    /// install markers with a framework-config override; additive, binary-compatible
    /// with plugins built against ≤1.11.0.
    /// 1.11.0 consolidates the portraits/replay line: IDungeonState
    /// (settlement + outcome + achieved score), IEntityTransforms (live entity transforms for
    /// position/replay capture), IEntityDetail.RefreshSocialSnapshot (self social-data refresh),
    /// IGameDataWorld.GetMonsterByEntity, SocialSnapshot.MasterScore, GearInstance.BreakThroughTime,
    /// skill-phase CombatEvents — all additive (new services / init-props / defaulted params),
    /// binary-compatible with plugins built against 1.10.0.
    /// 1.10.0 adds additive window-framework support behind the CooldownBar
    /// overlay: <c>WindowSpec.BackgroundOpacity</c> (poll-diffed backdrop on the borderless root that
    /// expands on height resize), <c>ColumnElement.Padding</c>, <c>RowElement.Justify</c> +
    /// <c>RowJustify</c> (with a compat overload), <c>BackdropElement</c>, <c>VirtualListElement.ResetScroll</c>,
    /// and <c>CooldownTileElement.OnClick</c> — all init-props / new records / defaulted params, so
    /// binary-compatible. Infrastructure: atomic game-asset icon rebind via a <c>WindowToken</c> binding (no
    /// scroll blink), buff icons as atlas Sprites, a <c>ConditionalElement</c> flex clamp, and a VirtualList
    /// viewport inset. (#32)
    /// 1.9.1 is a fix: <c>PandaMenuStateProbe</c> now also detects NPC
    /// dialogue, loading screens, the dungeon-enter confirm popup (<c>team_copy_popup</c> on
    /// <c>UILayerFuncPopup</c>), the line-selector panel, and story cutscenes as full-screen menu
    /// states, so <c>AutoHideBehindGameMenus</c> windows hide in all those cases.
    /// 1.9.0 adds <c>IWindowControl.BringToFront()</c> (a <c>ZFront</c> counter
    /// that sorts above category so explicit fronting works cross-category, with a pending flag so
    /// <c>SetVisible(true)</c> + <c>BringToFront()</c> works on a still-hidden window), a front-window
    /// interaction pass-through guard, and a restyled dropdown item to match <c>SelectableElement</c>.
    /// 1.8.0 adds <c>DropdownElement</c> — a reusable compact dropdown (trigger
    /// caption + ▾ that opens a themed floating option list above the window's scroll clip; dismiss on pick,
    /// outside-click, or Escape). The Settings → Performance per-plugin <b>Self-rate</b> control now uses it
    /// in place of the click-to-cycle button. 1.7.1 was a binary-compatibility hotfix: <c>SliderElement</c>'s
    /// <c>Width</c>/<c>HandleSize</c> moved off the record primary constructor (added in 1.7.0, which
    /// broke the old positional ctor) to init-only properties, so plugins compiled against ≤1.6.0
    /// (e.g. AutoFishing) load again. 1.7.0 adds <b>per-plugin &amp; dynamic update-rate control</b>:
    /// each plugin's <c>IFramework.Update</c> ticks at its own rate (user-configurable in
    /// Settings → Performance), and a plugin may temporarily ramp its own rate via
    /// <c>IFramework.RequestUpdateRate</c> (returns an <c>IUpdateRateScope</c>), gated by a
    /// per-plugin user permission. The framework tick became a single variable-speed clock at
    /// <c>max(global, all active rates)</c>, with the Lua-bridge probe drains riding it so RPC
    /// latency falls with the rate. Also adds <c>SliderElement.Width</c>/<c>HandleSize</c> and
    /// honors <c>ToggleElement.Enabled</c>. 1.6.0 adds <c>IExchange</c> (player Trading-Center
    /// query + buy via the game's own trade flow, exposed as <c>IPluginServices.Market</c>)
    /// and the extensible <c>INotifications.Create()</c> toast builder (custom-icon support).
    /// 1.5.0 added team-voice mic status + dungeon
    /// ready-check on the meter row (<c>IPartyEvents</c>/<c>IPartyRoster</c>) plus
    /// the click-away popup / <c>PanelElement</c> / z-ordering UI primitives. 1.4.2
    /// was a hotfix for non-ASCII (e.g. Thai) text input truncated in uGUI fields
    /// (onValidateInput → onValueChanged); 1.4.1 scoped the rail-button template
    /// lookup (periodic freeze); 1.4.0 added <c>IWindowControl.SetVisiblePersist</c>
    /// plus the native-UI grab-box / cutscene-reposition fixes.
    /// </summary>
    public const string Value = "1.14.0";
}
