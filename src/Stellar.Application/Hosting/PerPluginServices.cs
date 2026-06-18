using Stellar.Abstractions.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="IPluginServices"/> view. Delegates every property
/// to the shared services bag except <see cref="Config"/>, which is unique
/// to the plugin's GUID. PluginHost constructs one of these per loaded
/// plugin and passes it to the plugin's constructor so each plugin reads
/// and writes its own <c>&lt;pluginGuid&gt;.config.json</c> file.
/// </summary>
internal sealed class PerPluginServices : IPluginServices
{
    private readonly IPluginServices _shared;

    public PerPluginServices(IPluginServices shared, IPluginConfig perPluginConfig)
    {
        _shared = shared;
        Config = perPluginConfig;
    }

    public IPluginConfig Config { get; }

    public IPluginLog Log => _shared.Log;
    public IFramework Framework => _shared.Framework;
    public IClientState ClientState => _shared.ClientState;
    public IGameData GameData => _shared.GameData;
    public IPlayerStats PlayerStats => _shared.PlayerStats;
    public IInventory Inventory => _shared.Inventory;
    public IModuleEquip ModuleEquip => _shared.ModuleEquip;
    public ILoadout Loadout => _shared.Loadout;
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
    public IHotkeys Hotkeys => _shared.Hotkeys;
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
}
