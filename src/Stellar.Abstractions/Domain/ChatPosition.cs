namespace Stellar.Abstractions.Domain;

/// <summary>
/// A map position as the game shares it in chat (<c>PlaceHolderScenePosition</c>): scene, line (channel), whole-number
/// server coordinates and the minimap layer. Received on <see cref="ChatTextSegment.Position"/> for a position link,
/// returned by <see cref="Services.IChatPositionShare.GetSharePosition"/>, and accepted by
/// <see cref="Services.IChatPositionShare.TrackPosition"/>. Init-only so it can grow without a binary break.
/// </summary>
public sealed record ChatPosition
{
    /// <summary>SceneTable id (the map the position is on).</summary>
    public int SceneId { get; init; }

    /// <summary>Scene line (channel) number the sharer was on; 0 when unknown.</summary>
    public int LineId { get; init; }

    /// <summary>Server X coordinate, rounded up to a whole number by the sharer's client.</summary>
    public int X { get; init; }

    /// <summary>Server Y coordinate (height), rounded up to a whole number.</summary>
    public int Y { get; init; }

    /// <summary>Server Z coordinate, rounded up to a whole number.</summary>
    public int Z { get; init; }

    /// <summary>Minimap layer id (multi-floor maps); 0 on single-layer maps.</summary>
    public int MapLayerId { get; init; }

    /// <summary>The sharer's scene instance guid (new in Season 4; the game passes it to the tracker so a same-line
    /// share targets the same instance). Null when the wire record carried none.</summary>
    public string? SceneGuid { get; init; }

    /// <summary>Localised scene name (SceneTable <c>Name</c>), or null when the scene table hasn't loaded / the id is
    /// unknown. Ignored by <see cref="Services.IChatPositionShare.TrackPosition"/>.</summary>
    public string? SceneName { get; init; }
}

/// <summary>
/// A chat voice message (<c>ChatVoice</c>, msg_type Voice). The audio itself is not exposed; the server-side
/// speech-to-text transcript IS on the wire, so a plugin can show "voice · 5 s · transcript".
/// </summary>
public sealed record ChatVoice
{
    /// <summary>Recording length in whole seconds.</summary>
    public int Seconds { get; init; }

    /// <summary>Speech-to-text transcript (sanitised like chat text: rich tags other than <c>&lt;sprite=N&gt;</c>
    /// removed). Empty when the game produced none.</summary>
    public string Transcript { get; init; } = string.Empty;

    /// <summary>The game's voice-file id (cloud storage key). Informational; empty when absent.</summary>
    public string FileId { get; init; } = string.Empty;
}
