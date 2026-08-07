# IPluginServices interface

The single object passed to every plugin's constructor. Plugins obtain all framework capabilities by reading sub-services from here.

```csharp
public interface IPluginServices
```

## Members

| name | description |
| --- | --- |
| [Chat](IPluginServices/Chat.md) { get; } | Chat message stream and send API. |
| [ClientState](IPluginServices/ClientState.md) { get; } | Player session state (logged-in, current scene, login/logout events). |
| [CombatEvents](IPluginServices/CombatEvents.md) { get; } | Real-time combat event stream (damage, buffs, skill casts). |
| [CombatLookup](IPluginServices/CombatLookup.md) { get; } | Lookup service for static combat data (skill tables, buff tables). |
| [CombatSnapshot](IPluginServices/CombatSnapshot.md) { get; } | Snapshot of the most-recent combat state for all tracked entities. |
| [CombatSpec](IPluginServices/CombatSpec.md) { get; } | Per-entity active sub-profession (spec), resolved from observed combat casts. |
| [Config](IPluginServices/Config.md) { get; } | Plugin-scoped persistent configuration (JSON-backed key-value store). |
| [Data](IPluginServices/Data.md) { get; } | Per-plugin binary file storage for data too large/opaque for [`Config`](./IPluginServices/Config.md) (e.g. re-upload payloads). |
| [Dungeon](IPluginServices/Dungeon.md) { get; } | Current dungeon run: per-run unique id (`level_uuid`) and clear-time/score once the run settles. |
| [EntityContextMenu](IPluginServices/EntityContextMenu.md) { get; } | Row context-menu extension point (register entity-scoped menu items). |
| [EntityDetail](IPluginServices/EntityDetail.md) { get; } | Per-entity detail (full attribute map + equipment) for the inspector. |
| [EntityPortrait](IPluginServices/EntityPortrait.md) { get; } | Live 3D portrait of the local player (Entity Inspector). Self-only in v1. |
| [EntityTransforms](IPluginServices/EntityTransforms.md) { get; } | Reads live world transforms (position + facing) of entities by id — for replay/position capture. |
| [Exchange](IPluginServices/Exchange.md) { get; } | The inter-plugin communication channel — a plugin offers a contract via `Provide<T>` and another consumes it via `Consume<T>`, without referencing each other. The ONE generic extension point for plugin-to-plugin cooperation; specific contracts live in a shared contracts assembly. |
| [Framework](IPluginServices/Framework.md) { get; } | Framework lifecycle events and the per-tick Update callback. |
| [GameAssets](IPluginServices/GameAssets.md) { get; } | Game asset loader — profession/class icons and other atlased UI sprites. |
| [GameData](IPluginServices/GameData.md) { get; } | Read-only lookup over the game's static table data (skills, buffs, items, etc.). |
| [GameEnvironment](IPluginServices/GameEnvironment.md) { get; } | Region + version identity of the running game install (SEA / JP), detected once at boot. |
| [GameEvents](IPluginServices/GameEvents.md) { get; } | Game lifecycle events (scene load / unload, hot-update ready). |
| [Harmony](IPluginServices/Harmony.md) { get; } | Per-plugin Harmony host — create id-namespaced Harmony instances that are auto-unpatched when the plugin is disposed. |
| [Hotkeys](IPluginServices/Hotkeys.md) { get; } | Bindable keyboard-action registration. |
| [Inventory](IPluginServices/Inventory.md) { get; } | Player inventory — item stacks and currency. |
| [Launcher](IPluginServices/Launcher.md) { get; } | Register a tile in the Stellar launcher menu (Phase B). |
| [Loadout](IPluginServices/Loadout.md) { get; } | Read and apply the player's saved in-game loadouts (class + gear + spec + modules). |
| [Log](IPluginServices/Log.md) { get; } | Plugin-scoped log sink; output is routed to the BepInEx log with the plugin name as prefix. |
| [Lua](IPluginServices/Lua.md) { get; } | Bridge to the game's live tolua# Lua state (run chunks, read simple globals back). Main-thread only. |
| [Market](IPluginServices/Market.md) { get; } | The in-game player exchange/marketplace: query listings/care-list/notice items and buy through the game's own trade system. (Named `Market` because [`Exchange`](./IPluginServices/Exchange.md) is the inter-plugin channel.) |
| [ModuleEquip](IPluginServices/ModuleEquip.md) { get; } | Module equip actions (install / uninstall equipment modules). |
| [NamedTheme](IPluginServices/NamedTheme.md) { get; } | Theme preset selector and global font scale. |
| [NativeUi](IPluginServices/NativeUi.md) { get; } | Inject declarative mod uGUI into game-UI anchors (Phase 9d). |
| [NoticeTips](IPluginServices/NoticeTips.md) { get; } | Trigger the game's noticetip system (dungeon bars, win/fail banners, pop-up tips) with full control over content and audio. |
| [Notifications](IPluginServices/Notifications.md) { get; } | Show short transient on-screen toasts (plugin-side feedback the game does not surface itself). |
| [PartyControl](IPluginServices/PartyControl.md) { get; } | Party control — switch the party between 5- and 20-player via the game's own dispatcher. |
| [PartyEvents](IPluginServices/PartyEvents.md) { get; } | Party lifecycle events (member join/leave, leader change). |
| [PartyRoster](IPluginServices/PartyRoster.md) { get; } | Party roster — member list and group/slot information. |
| [PartySnapshot](IPluginServices/PartySnapshot.md) { get; } | Snapshot of the current party roster and member vitals. |
| [PlayerState](IPluginServices/PlayerState.md) { get; } | Local player's real-time state: name, level, HP, stamina, position. |
| [PlayerStats](IPluginServices/PlayerStats.md) { get; } | Live character attribute snapshot (ATK, DEF, crit rate, etc.). |
| [ProfileCardActions](IPluginServices/ProfileCardActions.md) { get; } | Contribute buttons to the game's native profile card action bar (the framework injects + styles them). |
| [Resonance](IPluginServices/Resonance.md) { get; } | Local player's equipped Battle Imagines (Resonance Skills), in slot order. |
| [ResonanceData](IPluginServices/ResonanceData.md) { get; } | Static game-data lookups for Battle Imagines (Resonance Skills): display + cooldown/charge info, and cast-skill → resonance reverse mapping. |
| [Theme](IPluginServices/Theme.md) { get; } | Active theme palette, text helpers, and colour registry. |
| [Windows](IPluginServices/Windows.md) { get; } | uGUI interactive window toolkit (SP1 window shell). |

## See Also

* namespace [Stellar.Abstractions.Services](../Stellar.Abstractions.md)

<!-- DO NOT EDIT: generated by xmldocmd for Stellar.Abstractions.dll -->
