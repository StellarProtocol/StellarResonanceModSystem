namespace Stellar.Abstractions.Services;

/// <summary>
/// The single object passed to every plugin's constructor. Plugins obtain all framework
/// capabilities by reading sub-services from here.
/// </summary>
public interface IPluginServices
{
    /// <summary>Plugin-scoped log sink; output is routed to the BepInEx log with the plugin name as prefix.</summary>
    IPluginLog Log { get; }
    /// <summary>Framework lifecycle events and the per-tick Update callback.</summary>
    IFramework Framework { get; }
    /// <summary>Player session state (logged-in, current scene, login/logout events).</summary>
    IClientState ClientState { get; }
    /// <summary>Read-only lookup over the game's static table data (skills, buffs, items, etc.).</summary>
    IGameData GameData { get; }
    /// <summary>Live character attribute snapshot (ATK, DEF, crit rate, etc.).</summary>
    IPlayerStats PlayerStats { get; }
    /// <summary>Player inventory — item stacks and currency.</summary>
    IInventory Inventory { get; }
    /// <summary>Module equip actions (install / uninstall equipment modules).</summary>
    IModuleEquip ModuleEquip { get; }
    /// <summary>Read and apply the player's saved in-game loadouts (class + gear + spec + modules).</summary>
    ILoadout Loadout { get; }
    /// <summary>The in-game player exchange/marketplace: query listings/care-list/notice items and
    /// buy through the game's own trade system. (Named <c>Market</c> because <see cref="Exchange"/>
    /// is the inter-plugin channel.)</summary>
    IExchange Market { get; }
    /// <summary>Show short transient on-screen toasts (plugin-side feedback the game does not surface itself).</summary>
    INotifications Notifications { get; }
    /// <summary>Plugin-scoped persistent configuration (JSON-backed key-value store).</summary>
    IPluginConfig Config { get; }
    /// <summary>Game lifecycle events (scene load / unload, hot-update ready).</summary>
    IGameEvents GameEvents { get; }
    /// <summary>Local player's real-time state: name, level, HP, stamina, position.</summary>
    IPlayerState PlayerState { get; }
    /// <summary>Chat message stream and send API.</summary>
    IChat Chat { get; }
    /// <summary>Snapshot of the most-recent combat state for all tracked entities.</summary>
    ICombatSnapshot CombatSnapshot { get; }
    /// <summary>Lookup service for static combat data (skill tables, buff tables).</summary>
    ICombatLookup CombatLookup { get; }
    /// <summary>Real-time combat event stream (damage, buffs, skill casts).</summary>
    ICombatEvents CombatEvents { get; }
    /// <summary>Per-entity active sub-profession (spec), resolved from observed combat casts.</summary>
    ICombatSpec CombatSpec { get; }
    /// <summary>Snapshot of the current party roster and member vitals.</summary>
    IPartySnapshot PartySnapshot { get; }
    /// <summary>Party roster — member list and group/slot information.</summary>
    IPartyRoster PartyRoster { get; }
    /// <summary>Party lifecycle events (member join/leave, leader change).</summary>
    IPartyEvents PartyEvents { get; }
    /// <summary>Party control — switch the party between 5- and 20-player via the game's own dispatcher.</summary>
    IPartyControl PartyControl { get; }
    /// <summary>Active theme palette, text helpers, and colour registry.</summary>
    ITheme Theme { get; }
    /// <summary>Bindable keyboard-action registration.</summary>
    IHotkeys Hotkeys { get; }
    /// <summary>Theme preset selector and global font scale.</summary>
    INamedTheme NamedTheme { get; }
    /// <summary>Inject declarative mod uGUI into game-UI anchors (Phase 9d).</summary>
    INativeUiHost NativeUi { get; }
    /// <summary>uGUI HUD toolkit.</summary>
    IHudHost Hud { get; }
    /// <summary>uGUI interactive window toolkit (SP1 window shell).</summary>
    IWindowHost Windows { get; }
    /// <summary>Register a tile in the Stellar launcher menu (Phase B).</summary>
    ILauncher Launcher { get; }
    /// <summary>Game asset loader — profession/class icons and other atlased UI sprites.</summary>
    IGameAssets GameAssets { get; }
    /// <summary>Local player's equipped Battle Imagines (Resonance Skills), in slot order.</summary>
    IResonanceState Resonance { get; }
    /// <summary>Static game-data lookups for Battle Imagines (Resonance Skills): display + cooldown/charge info, and cast-skill → resonance reverse mapping.</summary>
    IGameDataResonance ResonanceData { get; }
    /// <summary>Per-entity detail (full attribute map + equipment) for the inspector.</summary>
    IEntityDetail EntityDetail { get; }
    /// <summary>Row context-menu extension point (register entity-scoped menu items).</summary>
    IEntityContextMenu EntityContextMenu { get; }
    /// <summary>Live 3D portrait of the local player (Entity Inspector). Self-only in v1.</summary>
    IEntityPortrait EntityPortrait { get; }
    /// <summary>Contribute buttons to the game's native profile card action bar (the framework injects + styles them).</summary>
    IProfileCardActions ProfileCardActions { get; }
    /// <summary>The inter-plugin communication channel — a plugin offers a contract via <c>Provide&lt;T&gt;</c> and
    /// another consumes it via <c>Consume&lt;T&gt;</c>, without referencing each other. The ONE generic extension
    /// point for plugin-to-plugin cooperation; specific contracts live in a shared contracts assembly.</summary>
    IPluginExchange Exchange { get; }
    /// <summary>Trigger the game's noticetip system (dungeon bars, win/fail banners, pop-up tips) with full control over content and audio.</summary>
    INoticeTips NoticeTips { get; }
    /// <summary>Current dungeon run: per-run unique id (<c>level_uuid</c>) and clear-time/score once the run settles.</summary>
    IDungeonState Dungeon { get; }
}
