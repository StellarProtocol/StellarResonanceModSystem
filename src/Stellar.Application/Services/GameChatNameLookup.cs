using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Production <see cref="IChatNameLookup"/>: item / scene / buff names from the framework's already-cached,
/// localised <see cref="IGameData"/> (dictionary reads, no reflection), the rest from <see cref="IChatLinkTables"/>
/// (keyed reflection, cached per id). Either source may be null (tests / a host without game tables) → misses.
/// </summary>
internal sealed class GameChatNameLookup : IChatNameLookup
{
    private readonly IGameData? _gameData;
    private readonly IChatLinkTables? _tables;

    public GameChatNameLookup(IGameData? gameData, IChatLinkTables? tables)
    {
        _gameData = gameData;
        _tables = tables;
    }

    public ChatHyperlinkRow? GetHyperlink(int configId) => _tables?.GetHyperlink(configId);

    public bool TryGetItem(int itemId, out string name, out int quality)
    {
        if (_gameData?.Inventory.GetItem(itemId) is { } item)
        {
            name = item.Name;
            quality = item.Quality;
            return true;
        }
        name = string.Empty;
        quality = 0;
        return false;
    }

    public string? GetSceneName(int sceneId) => NonEmpty(_gameData?.World.GetScene(sceneId)?.Name);

    public string? GetBuffName(int buffId) => NonEmpty(_gameData?.Combat.GetBuff(buffId)?.Name);

    public string? GetUnionBuildingName(int buildingId) => _tables?.GetUnionBuildingName(buildingId);

    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>No game tables: every lookup misses, so the builder uses its fallbacks (unit tests, early boot).</summary>
internal sealed class NullChatNameLookup : IChatNameLookup
{
    public static readonly NullChatNameLookup Instance = new();

    public ChatHyperlinkRow? GetHyperlink(int configId) => null;

    public bool TryGetItem(int itemId, out string name, out int quality)
    {
        name = string.Empty;
        quality = 0;
        return false;
    }

    public string? GetSceneName(int sceneId) => null;

    public string? GetBuffName(int buffId) => null;

    public string? GetUnionBuildingName(int buildingId) => null;
}
