using Stellar.Abstractions.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="IPluginServices"/> view. Delegates every property
/// to the shared services bag except <see cref="Config"/> and <see cref="Data"/>,
/// which are unique to the plugin's GUID, <see cref="Framework"/>, which is
/// a per-plugin facade keyed by the plugin's GUID and driven by the
/// <see cref="Services.TickScheduler"/>, and <see cref="Hotkeys"/>, which tags
/// every declared action with the plugin's GUID.
/// PluginHost constructs one of these per loaded plugin and passes it to the
/// plugin's constructor so each plugin reads and writes its own
/// <c>&lt;pluginGuid&gt;.config.json</c> file, its own <c>&lt;pluginGuid&gt;.data/</c>
/// directory, and receives Update events at its own effective rate.
/// </summary>
internal sealed class PerPluginServices : IPluginServices
{
    private readonly IPluginServices _shared;
    private readonly IFramework _framework;
    // Null when the shared hotkey service doesn't expose the owner-tagged declaration
    // sink (a bare IHotkeys, e.g. in a test host). Falls back to the shared service,
    // and those actions group by id prefix exactly as they did before.
    private readonly IHotkeys? _hotkeys;
    // Per-plugin Harmony host (id-namespaced to this plugin, auto-unpatched on dispose). Null in a bare test
    // host that supplies no per-plugin host — falls back to the shared bag's host.
    private readonly IHarmonyHost? _harmony;

    public PerPluginServices(IPluginServices shared, IPluginConfig perPluginConfig, IFramework perPluginFramework,
                             IPluginDataStore perPluginData, IHotkeys? perPluginHotkeys = null,
                             IHarmonyHost? perPluginHarmony = null)
    {
        _shared = shared;
        Config = perPluginConfig;
        _framework = perPluginFramework;
        Data = perPluginData;
        _hotkeys = perPluginHotkeys;
        _harmony = perPluginHarmony;
    }

    public IPluginConfig Config { get; }
    public IPluginDataStore Data { get; }

    public IPluginLog Log => _shared.Log;
    public IFramework Framework => _framework;
    public IClientState ClientState => _shared.ClientState;
    public IGameData GameData => _shared.GameData;
    public IPlayerStats PlayerStats => _shared.PlayerStats;
    public IInventory Inventory => _shared.Inventory;
    public IModuleEquip ModuleEquip => _shared.ModuleEquip;
    public ILoadout Loadout => _shared.Loadout;
    public IExchange Market => _shared.Market;
    public INotifications Notifications => _shared.Notifications;
    public IGameEvents GameEvents => _shared.GameEvents;
    public IPlayerState PlayerState => _shared.PlayerState;
    public IChat Chat => _shared.Chat;
    public ICombatSnapshot CombatSnapshot => _shared.CombatSnapshot;
    public ICombatLookup CombatLookup => _shared.CombatLookup;
    public ICombatEvents CombatEvents => _shared.CombatEvents;
    public ICombatSpec CombatSpec => _shared.CombatSpec;
    public IPartySnapshot PartySnapshot => _shared.PartySnapshot;
    public IPartyRoster PartyRoster => _shared.PartyRoster;
    public IPartyEvents PartyEvents => _shared.PartyEvents;
    public IPartyControl PartyControl => _shared.PartyControl;
    public ITheme Theme => _shared.Theme;
    public IHotkeys Hotkeys => _hotkeys ?? _shared.Hotkeys;
    public INamedTheme NamedTheme => _shared.NamedTheme;
    public INativeUiHost NativeUi => _shared.NativeUi;
    public IHudHost Hud => _shared.Hud;
    public IWindowHost Windows => _shared.Windows;
    public ILauncher Launcher => _shared.Launcher;
    public IGameAssets GameAssets => _shared.GameAssets;
    public IResonanceState Resonance => _shared.Resonance;
    public IGameDataResonance ResonanceData => _shared.ResonanceData;
    public IEntityDetail EntityDetail => _shared.EntityDetail;
    public IEntityContextMenu EntityContextMenu => _shared.EntityContextMenu;
    public IEntityPortrait EntityPortrait => _shared.EntityPortrait;
    public IProfileCardActions ProfileCardActions => _shared.ProfileCardActions;
    public IPluginExchange Exchange => _shared.Exchange;
    public INoticeTips NoticeTips => _shared.NoticeTips;
    public IDungeonState Dungeon => _shared.Dungeon;
    public IEntityTransforms EntityTransforms => _shared.EntityTransforms;
    public IGameEnvironment GameEnvironment => _shared.GameEnvironment;
    public ILua Lua => _shared.Lua;
    public IHarmonyHost Harmony => _harmony ?? _shared.Harmony;
}
