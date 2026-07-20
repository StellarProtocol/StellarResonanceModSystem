using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>Concrete aggregator. Constructed once at boot and handed to every plugin.</summary>
internal sealed class PluginServices : IPluginServices
{
    public IPluginLog Log { get; }
    public IFramework Framework { get; }
    public IClientState ClientState { get; }
    public IGameData GameData { get; }
    public IPlayerStats PlayerStats { get; }
    public IInventory Inventory { get; }
    public IModuleEquip ModuleEquip { get; }
    public ILoadout Loadout { get; }
    public IExchange Market { get; }
    public INotifications Notifications { get; }
    public IPluginConfig Config { get; }
    public IPluginDataStore Data { get; }
    public IGameEvents GameEvents { get; }
    public IPlayerState PlayerState { get; }
    public IChat Chat { get; }
    public ICombatSnapshot CombatSnapshot { get; }
    public ICombatLookup   CombatLookup   { get; }
    public ICombatEvents   CombatEvents   { get; }
    public ICombatSpec     CombatSpec     { get; }
    public IPartySnapshot PartySnapshot { get; }
    public IPartyRoster   PartyRoster   { get; }
    public IPartyEvents   PartyEvents   { get; }
    public IPartyControl  PartyControl  { get; }
    public ITheme Theme { get; }
    public IHotkeys Hotkeys { get; }
    public INamedTheme NamedTheme { get; }
    public INativeUiHost NativeUi { get; }
    public IHudHost Hud { get; }
    public IWindowHost Windows { get; }
    public ILauncher Launcher { get; }
    public IGameAssets GameAssets { get; }
    public IResonanceState Resonance { get; }
    public IGameDataResonance ResonanceData { get; }
    public IEntityDetail EntityDetail { get; }
    public IEntityContextMenu EntityContextMenu { get; }
    public IEntityPortrait EntityPortrait { get; }
    public IProfileCardActions ProfileCardActions { get; }
    public IPluginExchange Exchange { get; }
    public INoticeTips NoticeTips { get; }
    public IDungeonState Dungeon { get; }
    public IEntityTransforms EntityTransforms { get; }
    public IGameEnvironment GameEnvironment { get; }

    public PluginServices(
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        IGameData gameData,
        IPlayerStats playerStats,
        IInventory inventory,
        IModuleEquip moduleEquip,
        ILoadout loadout,
        IExchange market,
        INotifications notifications,
        IPluginConfig config,
        IGameEvents gameEvents,
        IPlayerState playerState,
        IChat chat,
        ICombatSnapshot combatSnapshot,
        ICombatLookup   combatLookup,
        ICombatEvents   combatEvents,
        ICombatSpec     combatSpec,
        IPartySnapshot partySnapshot,
        IPartyRoster   partyRoster,
        IPartyEvents   partyEvents,
        IPartyControl  partyControl,
        ITheme theme,
        IHotkeys hotkeys,
        INamedTheme namedTheme,
        INativeUiHost nativeUi,
        IHudHost hud,
        IWindowHost windows,
        ILauncher launcher,
        IGameAssets gameAssets,
        IResonanceState resonance,
        IGameDataResonance resonanceData,
        IEntityDetail entityDetail,
        IEntityContextMenu entityContextMenu,
        IEntityPortrait entityPortrait,
        IProfileCardActions profileCardActions,
        IPluginExchange exchange,
        INoticeTips noticeTips,
        IDungeonState dungeon,
        IEntityTransforms entityTransforms,
        IGameEnvironment gameEnvironment,
        IPluginDataStore data)
    {
        Log = log;
        Framework = framework;
        ClientState = clientState;
        GameData = gameData;
        PlayerStats = playerStats;
        Inventory = inventory;
        ModuleEquip = moduleEquip;
        Loadout = loadout;
        Market = market;
        Notifications = notifications;
        Config = config;
        GameEvents = gameEvents;
        PlayerState = playerState;
        Chat = chat;
        CombatSnapshot = combatSnapshot;
        CombatLookup   = combatLookup;
        CombatEvents   = combatEvents;
        CombatSpec     = combatSpec;
        PartySnapshot  = partySnapshot;
        PartyRoster    = partyRoster;
        PartyEvents    = partyEvents;
        PartyControl   = partyControl;
        Theme = theme;
        Hotkeys = hotkeys;
        NamedTheme = namedTheme;
        NativeUi = nativeUi;
        Hud = hud;
        Windows = windows;
        Launcher = launcher;
        GameAssets = gameAssets;
        Resonance = resonance;
        ResonanceData = resonanceData;
        EntityDetail = entityDetail;
        EntityContextMenu = entityContextMenu;
        EntityPortrait = entityPortrait;
        ProfileCardActions = profileCardActions;
        Exchange = exchange;
        NoticeTips = noticeTips;
        Dungeon = dungeon;
        EntityTransforms = entityTransforms;
        GameEnvironment = gameEnvironment;
        Data = data;
    }
}
