using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Map-position chat links: share the local player's position the way the game's chat "Location" tab does, and
/// follow a received position link the way clicking it in the game's chat does. Reached through
/// <see cref="IChat"/> (<c>services.Chat.SharePosition(...)</c>), which inherits it. Since framework 2.35.0.
/// <para><b>Main thread only</b> — every member drives the game's Lua. Call from a UI handler, <see cref="IFramework.Update"/>
/// or <see cref="IFrameworkTiming.Post"/>; off-thread calls return <see cref="ChatShareStatus.WrongThread"/> and do nothing.
/// These are user-action calls (a button press), not something to poll every frame.</para>
/// </summary>
public interface IChatPositionShare
{
    /// <summary>Reads what <see cref="SharePosition"/> would send now — scene, line, rounded coordinates, layer, scene
    /// name — and whether this map allows sharing. One short Lua call; fine to refresh a few times a second while a
    /// share UI is open, not every frame.</summary>
    ChatPositionPreview GetSharePosition() => new();

    /// <summary>Shares the local player's current position to <paramref name="target"/> as a real game position link
    /// (the server builds the link message; every client — this one included — receives it as a hypertext chat message
    /// with <see cref="ChatHypertext.Kind"/> = <see cref="ChatLinkKind.Position"/>). <paramref name="textBefore"/> is
    /// optional text shown before the link (rich tags except <c>&lt;sprite=N&gt;</c> are stripped).
    /// <para>Returns <see cref="ChatShareStatus.Ok"/> once the request has left the client; the server's verdict then
    /// arrives on <see cref="IChat.SendResult"/> (<see cref="ChatSendResult.IsPositionShare"/> = true), and on a refusal
    /// the game also shows its own tip. <see cref="ChatShareStatus.Refused"/> when the map forbids sharing (see
    /// <see cref="GetSharePosition"/>); <see cref="ChatShareStatus.Unsupported"/> for a target that can't carry a share.</para></summary>
    /// <param name="target">Say (the game's "Current"), World, Party, Guild or Whisper.</param>
    /// <param name="textBefore">Text before the link, or null for none.</param>
    ChatShareResult SharePosition(ChatTarget target, string? textBefore)
        => new() { Status = ChatShareStatus.NotReady };

    /// <summary>Does what clicking a position link in the game's chat does: sets a map waypoint on
    /// <paramref name="position"/> and opens the map (no teleport, no server call). Refused when the target map is
    /// locked for this character or can't be navigated to from here (the game then shows its own tip as well, as for a
    /// native click; the result carries the same reason). Pass <see cref="ChatTextSegment.Position"/> from a received
    /// message, or build one.</summary>
    /// <param name="position">Where to set the waypoint.</param>
    ChatShareResult TrackPosition(ChatPosition position)
        => new() { Status = ChatShareStatus.NotReady };
}
