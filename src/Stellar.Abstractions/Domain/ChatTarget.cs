using System;

namespace Stellar.Abstractions.Domain;

/// <summary>Sealed sum type identifying the destination for <c>IChat.Send</c>.</summary>
public abstract record ChatTarget
{
    private ChatTarget() { }

    /// <summary>Send to the local say / proximity channel.</summary>
    public static ChatTarget Say   { get; } = new SayTarget();
    /// <summary>Send to the server-wide world channel.</summary>
    public static ChatTarget World { get; } = new WorldTarget();
    /// <summary>Send to the current party channel.</summary>
    public static ChatTarget Party { get; } = new PartyTarget();
    /// <summary>Send to the guild / clan channel.</summary>
    public static ChatTarget Guild { get; } = new GuildTarget();
    /// <summary>Send a private whisper to the player whose charId is <paramref name="targetId"/>
    /// (the id a received <see cref="ChatMessage.SenderId"/> / <see cref="ChatMessage.PeerId"/> carries).</summary>
    public static ChatTarget Whisper(long targetId) => new WhisperTarget(targetId);

    /// <summary>Build a reply target matching the channel that <paramref name="source"/> arrived on. A whisper
    /// replies to the conversation PEER when known (so replying to your own echoed whisper reaches the
    /// recipient, not yourself), else to the sender.</summary>
    public static ChatTarget Reply(ChatMessage source) => source.Channel switch
    {
        ChatChannel.Whisper => Whisper(source.PeerId != 0 ? source.PeerId : source.SenderId),
        ChatChannel.Say     => Say,
        ChatChannel.Party   => Party,
        ChatChannel.Guild   => Guild,
        ChatChannel.World   => World,
        _ => throw new InvalidOperationException(
                 $"Cannot reply to channel {source.Channel}"),
    };

    /// <summary>Say / proximity channel target.</summary>
    public sealed record SayTarget    : ChatTarget;
    /// <summary>World channel target.</summary>
    public sealed record WorldTarget  : ChatTarget;
    /// <summary>Party channel target.</summary>
    public sealed record PartyTarget  : ChatTarget;
    /// <summary>Guild channel target.</summary>
    public sealed record GuildTarget  : ChatTarget;
    /// <summary>Private whisper target; <see cref="TargetId"/> is the recipient's charId (NOT an entity uuid —
    /// a charId is <c>uuid &gt;&gt; 16</c>). Written to the wire as <c>ChatMsgInfo.target_id</c>.</summary>
    /// <param name="TargetId">CharId of the whisper recipient.</param>
    public sealed record WhisperTarget(long TargetId) : ChatTarget;
}
