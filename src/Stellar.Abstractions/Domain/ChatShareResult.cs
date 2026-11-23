namespace Stellar.Abstractions.Domain;

/// <summary>Outcome of a position share / track request. Explicit values; append only.</summary>
public enum ChatShareStatus
{
    /// <summary>Done: the waypoint was set and the map opened (track), or the share request left the client (share —
    /// the server's verdict follows on <see cref="Services.IChat.SendResult"/>).</summary>
    Ok = 0,
    /// <summary>The game isn't ready (not in the world yet, Lua not up, no player entity). Nothing happened.</summary>
    NotReady = 1,
    /// <summary>Called off the Unity main thread. Nothing happened — marshal with <c>IFrameworkTiming.Post</c>.</summary>
    WrongThread = 2,
    /// <summary>The game refused it: the current map forbids sharing coordinates, or the target map is locked /
    /// can't be navigated to. <see cref="ChatShareResult.Message"/> carries the game's own reason text when known.</summary>
    Refused = 3,
    /// <summary>Unsupported target (only Say/World/Party/Guild/Whisper can carry a share) or invalid arguments.</summary>
    Unsupported = 4,
    /// <summary>The game's Lua threw. Nothing is known to have happened; details are in the framework log.</summary>
    Failed = 5,
}

/// <summary>Result of <see cref="Services.IChatPositionShare.SharePosition"/> /
/// <see cref="Services.IChatPositionShare.TrackPosition"/>.</summary>
public readonly record struct ChatShareResult
{
    /// <summary>What happened.</summary>
    public ChatShareStatus Status { get; init; }

    /// <summary>True when <see cref="Status"/> is <see cref="ChatShareStatus.Ok"/>.</summary>
    public bool Ok => Status == ChatShareStatus.Ok;

    /// <summary>The game's message id (MessageTable) for a refusal the game itself would show as a tip (102910
    /// "Coordinates cannot be shared in this scene", 102915 "Destination cannot be reached via auto-navigation");
    /// 0 when none.</summary>
    public int GameMessageId { get; init; }

    /// <summary>Localised reason text for a refusal (the game's MessageTable text when <see cref="GameMessageId"/> is
    /// set), else a short English developer string; null on success.</summary>
    public string? Message { get; init; }
}

/// <summary>What <see cref="Services.IChatPositionShare.SharePosition"/> would send right now: the local player's
/// position as the game rounds it, and whether the current map allows sharing it.</summary>
public sealed record ChatPositionPreview
{
    /// <summary>False when nothing could be read (not in the world, Lua not ready, no player entity);
    /// <see cref="Position"/> is then null.</summary>
    public bool Available { get; init; }

    /// <summary>The position a share would carry (scene, line, ceil'd x/y/z, minimap layer, scene name); null when
    /// <see cref="Available"/> is false. Its <see cref="ChatPosition.SceneGuid"/> is always null (the server fills it).</summary>
    public ChatPosition? Position { get; init; }

    /// <summary>True when the current map allows sharing coordinates (the game's own rule: a static/open-world scene
    /// not on the share blocklist). Dungeons, raids and instances are false.</summary>
    public bool CanShare { get; init; }

    /// <summary>When <see cref="CanShare"/> is false, the game's localised reason (MessageTable 102910); else null.</summary>
    public string? Reason { get; init; }
}
