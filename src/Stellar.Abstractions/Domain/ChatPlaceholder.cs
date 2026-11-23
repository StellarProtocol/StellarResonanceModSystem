namespace Stellar.Abstractions.Domain;

/// <summary>Kind of one raw hypertext slot. Values mirror the game's <c>zproto.PlaceHolderType</c> exactly (explicit,
/// so a future game type appended to the wire enum keeps every existing value).</summary>
public enum ChatPlaceholderType
{
    /// <summary>Unset / unrecognised wire type (its bytes are skipped).</summary>
    None = 0,
    /// <summary>A number (<see cref="ChatPlaceholder.Value"/>): a roll, a percentage, a level, a minute count.</summary>
    Val = 1,
    /// <summary>A player: <see cref="ChatPlaceholder.CharId"/> + name in <see cref="ChatPlaceholder.Text"/>.</summary>
    Player = 2,
    /// <summary>An item: config id in <see cref="ChatPlaceholder.Value"/>.</summary>
    Item = 3,
    /// <summary>A guild building: UnionBuildingTable id in <see cref="ChatPlaceholder.Value"/>.</summary>
    Union = 4,
    /// <summary>A buff: BuffTable id in <see cref="ChatPlaceholder.Value"/>.</summary>
    Buff = 5,
    /// <summary>A time: Unix SECONDS in <see cref="ChatPlaceholder.Value"/>.</summary>
    Timestamp = 6,
    /// <summary>Free text the sender typed before/after a share (<see cref="ChatPlaceholder.Text"/>, unsanitised).</summary>
    String = 7,
    /// <summary>Fishing profile share: angler name in <see cref="ChatPlaceholder.Text"/>.</summary>
    FishPersonalTotal = 8,
    /// <summary>Fishing record share: fish item id in <see cref="ChatPlaceholder.Value"/>, size in <see cref="ChatPlaceholder.Rank"/>.</summary>
    FishItem = 9,
    /// <summary>Fishing ranking share: fish item id in <see cref="ChatPlaceholder.Value"/>, rank in <see cref="ChatPlaceholder.Rank"/>.</summary>
    FishRank = 10,
    /// <summary>Guild group notice (no payload; the text is the message's own text).</summary>
    UnionGroup = 11,
    /// <summary>Master-mode seal share: player name in <see cref="ChatPlaceholder.Text"/>, season in <see cref="ChatPlaceholder.Value"/>.</summary>
    MasterMode = 12,
    /// <summary>A map position: <see cref="ChatPlaceholder.Position"/>.</summary>
    ScenePosition = 13,
    /// <summary>A home share: owner <see cref="ChatPlaceholder.CharId"/>, <see cref="ChatPlaceholder.HomelandId"/>,
    /// <see cref="ChatPlaceholder.CommunityId"/>, home name in <see cref="ChatPlaceholder.Text"/>.</summary>
    Homeland = 14,
    /// <summary>A homestead blueprint share: owner name in <see cref="ChatPlaceholder.Text"/>.</summary>
    Blueprint = 15,
}

/// <summary>
/// One raw slot of a hypertext chat message (<c>PlaceHolder</c>), decoded from its nested proto. Which members are
/// set depends on <see cref="Type"/> (see each <see cref="ChatPlaceholderType"/> member). Most plugins want the
/// ready-to-render <see cref="ChatHypertext.Segments"/> instead; this is the lossless source they were built from.
/// </summary>
public sealed record ChatPlaceholder
{
    /// <summary>Slot kind.</summary>
    public ChatPlaceholderType Type { get; init; }

    /// <summary>Number payload: Val value, Item/Fish config id, Union building id, Buff id, Timestamp (Unix seconds),
    /// MasterMode season. 0 when the type carries none.</summary>
    public long Value { get; init; }

    /// <summary>Player char id (Player) or the home owner's char id (Homeland); 0 otherwise.</summary>
    public long CharId { get; init; }

    /// <summary>Text payload: String text, Player / Homeland / Blueprint / angler / master name. Raw wire text — NOT
    /// sanitised (strip rich tags before rendering it yourself). Empty when the type carries none.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Position payload (ScenePosition); null otherwise. <see cref="ChatPosition.SceneName"/> is filled in
    /// on the delivered message when the scene table knows the id.</summary>
    public ChatPosition? Position { get; init; }

    /// <summary>Fish size (FishItem) or rank (FishRank); 0 otherwise.</summary>
    public int Rank { get; init; }

    /// <summary>Homeland id (Homeland); 0 otherwise.</summary>
    public long HomelandId { get; init; }

    /// <summary>Homeland community id (Homeland); 0 otherwise.</summary>
    public long CommunityId { get; init; }
}
