namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port: keyed reads of the game config tables that chat links need and <c>IGameData</c> doesn't cache
/// (ChatHyperlink templates, guild building names, MessageTable tip texts). Implemented in Infrastructure over
/// <c>Bokura.*Base.GetTable(true).TryGetValue(id, out row, false)</c>, cached per id. Main thread only.
/// </summary>
internal interface IChatLinkTables
{
    /// <summary>The ChatHyperlink row for <paramref name="configId"/>, or null when unknown / tables not loaded.</summary>
    ChatHyperlinkRow? GetHyperlink(int configId);

    /// <summary>UnionBuildingTable <c>BuildingName</c>, or null.</summary>
    string? GetUnionBuildingName(int buildingId);

    /// <summary>MessageTable <c>Content</c> (the text of a game tip such as 102910), or null.</summary>
    string? GetMessageText(int messageId);
}

/// <summary>The ChatHyperlink columns the chat-link builder uses (localised strings, as the live table holds them).</summary>
internal sealed record ChatHyperlinkRow(int Id, string Content, int FuncType, string FunctionButtonEscape);

/// <summary>
/// Every name lookup the hypertext builder needs, behind one seam so it is unit-testable without the game.
/// Production: <c>GameChatNameLookup</c> (IGameData + <see cref="IChatLinkTables"/>). Main thread only.
/// </summary>
internal interface IChatNameLookup
{
    /// <summary>ChatHyperlink row, or null.</summary>
    ChatHyperlinkRow? GetHyperlink(int configId);

    /// <summary>Item display name + quality tier; false when unknown.</summary>
    bool TryGetItem(int itemId, out string name, out int quality);

    /// <summary>SceneTable name, or null.</summary>
    string? GetSceneName(int sceneId);

    /// <summary>BuffTable name, or null.</summary>
    string? GetBuffName(int buffId);

    /// <summary>UnionBuildingTable name, or null.</summary>
    string? GetUnionBuildingName(int buildingId);
}
