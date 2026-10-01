namespace Stellar.Abstractions.Domain;

/// <summary>What kind of person a pose target is (posing by person, framework 2.15.0).</summary>
public enum PersonKind
{
    /// <summary>The local player, posed live (the game's own photo behaviour: an action start and a reset each ride one
    /// movement update; nothing else is sent).</summary>
    Self,
    /// <summary>Another player, posed as a local photo copy; the real player is hidden while the copy exists. Nothing
    /// is sent.</summary>
    Player,
    /// <summary>An NPC, posed as a locally generated model; the real NPC is hidden and never changed. Nothing is sent.</summary>
    Npc,
}

/// <summary>A person near the local player who can be posed.</summary>
/// <param name="Id">The entity (usable with <c>IEntityTransforms</c> and <c>IPosing.Select</c>).</param>
/// <param name="Name">Display name; empty when the game has none.</param>
/// <param name="Kind">Self, another player or an NPC.</param>
/// <param name="Distance">Metres from the local player.</param>
public sealed record PersonInfo(EntityId Id, string Name, PersonKind Kind, float Distance);

/// <summary>A facial expression the game offers (the emote table's expression rows). The face shown depends on the
/// model's gender; the framework picks the right one.</summary>
/// <param name="Id">The expression's emote table id.</param>
/// <param name="Name">Display name in the client's language.</param>
/// <param name="MaleFaceId">Face id used on a male model.</param>
/// <param name="FemaleFaceId">Face id used on a female model.</param>
public sealed record ExpressionInfo(int Id, string Name, int MaleFaceId, int FemaleFaceId);

/// <summary>Where a head or the eyes look (the game photo panel's three modes).</summary>
public enum LookMode
{
    /// <summary>The game decides (normal behaviour).</summary>
    Default,
    /// <summary>Toward the camera.</summary>
    Lens,
    /// <summary>Toward a point set with <c>IPoseTarget.Aim</c>.</summary>
    Free,
}

/// <summary>Which look a call changes.</summary>
public enum LookPart
{
    /// <summary>The head turn.</summary>
    Head,
    /// <summary>The eyes.</summary>
    Eyes,
}

/// <summary>Where a pose target stands.</summary>
public enum PoseTargetState
{
    /// <summary>Selected; nothing changed yet (no copy or model made).</summary>
    Idle,
    /// <summary>An NPC model is loading; controls are remembered and applied when it arrives.</summary>
    Loading,
    /// <summary>Posable.</summary>
    Ready,
    /// <summary>The copy or model could not be made; <c>Reset()</c> lets it try again.</summary>
    Failed,
    /// <summary>Released by the framework (free camera ended, the person left, or the plugin unloaded); select again.</summary>
    Released,
    /// <summary>The game's photo-member limit is reached for this kind of person (other players' copies, or NPC models);
    /// nothing was made. Reset someone else, then use a control again.</summary>
    Full,
}

/// <summary>Outcome of <c>IPoseTarget.PlayAction</c>.</summary>
public enum PoseResult
{
    /// <summary>Playing.</summary>
    Applied,
    /// <summary>The NPC model is still loading; it plays when the model arrives.</summary>
    Loading,
    /// <summary>The game's own check refused it (the game shows its own message).</summary>
    Refused,
    /// <summary>Not possible now (no free camera, the person left, or the copy/model failed).</summary>
    Unavailable,
    /// <summary>The photo-member limit is reached (see <see cref="PoseTargetState.Full"/>); nothing was made.</summary>
    Full,
}
