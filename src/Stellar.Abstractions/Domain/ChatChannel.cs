namespace Stellar.Abstractions.Domain;

/// <summary>Incoming-chat channel bucket. Mirrors a subset of <c>Zproto.ChitChatChannelType</c>.</summary>
public enum ChatChannel
{
    /// <summary>Channel type not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Local / proximity say channel.</summary>
    Say,
    /// <summary>Server-wide world chat channel.</summary>
    World,
    /// <summary>Current party channel.</summary>
    Party,
    /// <summary>Guild / clan channel.</summary>
    Guild,
    /// <summary>Private whisper channel.</summary>
    Whisper,
    /// <summary>Server system-notification channel.</summary>
    System,
}
