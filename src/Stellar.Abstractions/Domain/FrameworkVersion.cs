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
    /// BepInEx chainloader happy.
    /// 2.6.1 is a fix: <c>IWardrobe.GetWornWeaponSkin</c> reports "no weapon skin" as
    /// <c>SkinId 0</c> again. The Wardrobe's ⊘ tile is not skin 0 — the game stores the
    /// current weapon's ORIGIN row id (a concrete <c>WeaponSkinTable</c> row per weapon
    /// family), so capture saved a specific skin and re-applying the outfit forced that
    /// look. Infrastructure-only (capture chunk) — no API change, binary-compatible.
    /// 2.4.1 is a fix: the framework-only "Cannot perform this action during combat"
    /// toast (~every 5s in sustained open-world combat) is gone. Root cause: every
    /// CharSerialize merge re-armed the loadout refresh, whose weapon-VM
    /// <c>SyncProjectList</c> RPC the server rejects in combat (ErrStateIllegal 3202)
    /// and the game's own wrapper toasts. <c>PandaLoadoutProbe.DecideRefresh</c> now
    /// defers ONLY that RPC while the local player is in combat (game's own check),
    /// keeping the pending/first-refresh state armed so exactly one refresh fires at
    /// combat end; the RPC-free live-state re-read keeps running in combat.
    /// Infrastructure-only — no API change, binary-compatible with all existing plugins.
    /// (2.4.0 shipped the Wardrobe surface but did not bump this constant; 2.4.1
    /// realigns it.)
    /// 2.2.0 is the live-build release. Adds <c>ILoadout.LiveState</c> (<c>LiveLoadoutState</c> — the
    /// live class + talents, never a saved plan), <c>ILoadout.LiveStateChanged</c> (ONE game-tick event
    /// covering the whole build: equipped gear/module slots, class, talent stage/nodes, the equipped
    /// Battle Imagine pair, and Deep-Slumber — raised only after the framework's re-read actually changed
    /// what it serves, so a consumer may treat it as "the setup I can read right now is the new one"),
    /// and <c>IPluginServices.DeepSlumber</c> (<c>IDeepSlumber</c>) — the live Deep-Slumber Psychoscope
    /// (season cultivate) state: season levels, lines, areas, socketed cards and node levels, read live
    /// per call. <c>IInventory.SelfGearChanged</c> widens from gear-only to the local player's BUILD-state
    /// signal (it now also fires on talent field-61 and season-cultivate field-101 dirty deltas).
    /// <c>IResonanceState.Installed</c> keeps its shape but changes SOURCE and id space: the equipped
    /// Battle Imagines are now read from the skill hotbar's aoyi slots 7/8 as aoyi SKILL ids (resolve via
    /// <c>IGameDataResonance.GetImagineForSkill</c>) because <c>CharSerialize.resonance</c> (wire field 28)
    /// is never re-serialized on an in-session swap. Internally the framework's last per-tick game reads
    /// are gone — live-state capture and the dungeon defeated count are both event-driven now
    /// (container-merge / scene-attr sync). New members + a new service only — additive, binary-compatible
    /// with plugins built against ≤2.1.0.
    /// 2.1.0 adds <c>IPluginServices.Localization</c> (<c>ILocalization</c>) — a plugin-scoped UI-text
    /// localizer. A plugin ships four embedded <c>Lang/{en,ja,th,id}.json</c> catalogs and calls
    /// <c>Localization.T(key)</c> / <c>TFormat(key, args)</c>, resolved to the active language (English
    /// fallback, then the key literal). The framework auto-discovers each plugin's catalogs at load, and
    /// Stellar's own settings UI is now localized in English / 日本語 / ไทย / Bahasa Indonesia
    /// (Settings → Themes → Language, defaulting to Follow-game-client). Adds <c>ILocalizationControl</c>
    /// (Settings-facing, NOT on <c>IPluginServices</c>). New service only — additive, binary-compatible with
    /// plugins built against ≤2.0.3.
    /// 1.18.1 is a fix: game-region detection matches the running
    /// executable by name PREFIX (<c>StarSEA</c> = SEA, <c>StarASIA</c> = JP) rather than exact
    /// filename, so the Steam build's <c>StarSEA_STEAM.exe</c> resolves to SEA instead of Unknown
    /// (which had made upload plugins withhold every run). Detection-logic only — no API change,
    /// binary-compatible with all existing plugins.
    /// 1.18.0 adds <c>SliderElement.SquareHandle</c>, an OPT-IN knob that is
    /// exactly <c>HandleSize</c> square. Unity's <c>Slider</c> drives the handle's cross-axis anchors to
    /// full stretch every frame, so by default <c>HandleSize</c> ADDS to the row height instead of setting
    /// the knob's height — a 13px handle in a 16px row draws a 13×29 capsule (measured 2026-07-30). The
    /// stretched shape stays the default on purpose: every existing slider already renders that way, and
    /// correcting it globally would restyle every plugin's sliders at once. Additive only — no existing
    /// slider changes appearance, and drag behaviour is identical (the value maps from the container's
    /// width, which is untouched). 1.17.0 adds <c>ActorState</c> and
    /// <c>CombatEvent.EntityStateChanged</c> (2026-07-28 entity-state-death-signal spec):
    /// the client's own entity state-machine transitions (death, break phase), surfaced on
    /// the existing <c>ICombatEvents</c> stream — no service interface gains a member, so
    /// the STELLAR0005 8-member ceiling is untouched. Lets plugins (e.g. the CombatMeter's
    /// <c>BossKill</c>) know an entity died without inferring it from HP reaching zero,
    /// which scripted kills never do. Field-proven sourcing, after three recon rounds (see
    /// <c>recon/entity-state-death-signal-notes.md</c>): <c>Panda.ZGame.ZStateDead.OnEnter</c>
    /// fired for all ten deaths in the owner's confirming run and is the sole installed
    /// death source; <c>Panda.ZGame.ZStateBreaking.OnEnter</c> (untested, not disproven —
    /// no break phase occurred in that run) is its kept sibling. The originally-spec'd
    /// <c>EntityCtrlDead.OnEnter</c> stayed silent across those same ten deaths (disproven)
    /// and the wider <c>ZStateMachine.onStateChanged</c>/<c>EnterState</c> hooks tried in
    /// between were dropped for cost, not correctness — <c>EnterState</c> also resolved
    /// every one of the ten deaths correctly and remains a documented, field-proven fallback
    /// if <c>ZStateDead</c> is ever removed/renamed, just not installed by default because it
    /// fires on every actor's every transition rather than only the one that matters. New
    /// enum + new discriminated-union case only — additive, binary-compatible with plugins
    /// built against ≤1.16.1.
    /// 1.15.0 adds <c>IPluginServices.Data</c> — an
    /// <c>IPluginDataStore</c> giving each plugin its own binary file storage
    /// (<c>Write</c>/<c>Read</c>/<c>Delete</c>/<c>List</c>, never-throws, path-traversal-safe)
    /// for data too large/opaque for <c>IConfigSection</c>; rooted OUTSIDE the plugin-scan
    /// dir. New service only — additive, binary-compatible with plugins built against ≤1.14.0.
    /// 1.14.0 adds <c>EntityVitals.HasHpObservation</c> (MaxHp-only
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
    public const string Value = "2.7.0";
}
