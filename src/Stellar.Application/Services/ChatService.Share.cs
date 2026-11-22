using System;
using System.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// <see cref="IChatPositionShare"/>: preview / share / track a map position through the game's own Lua
/// (<see cref="ChatShareLua"/>). All main-thread, user-action calls. A share is an async World RPC, so its verdict is
/// parked by the Lua coroutine and picked up by <see cref="PollShareResult"/> (from <see cref="Drain"/>, ≤10 Hz, only
/// while one is pending) and raised on <see cref="SendResult"/> with <see cref="ChatSendResult.IsPositionShare"/>.
/// </summary>
internal sealed partial class ChatService
{
    private const int MsgCannotShareHere = 102910;   // "Coordinates cannot be shared in this scene"
    private const int MsgUnreachable = 102915;       // "Destination cannot be reached via auto-navigation"
    private const int MaxShareText = 200;
    private static readonly long SharePollTicks = Stopwatch.Frequency / 10;     // 10 Hz
    private static readonly long ShareTimeoutTicks = Stopwatch.Frequency * 15;  // RPC timeout is shorter

    private long _shareDeadline;     // Stopwatch ticks; 0 = no share pending
    private long _shareNextPoll;
    private ChatChannel _shareChannel;

    /// <summary>Game Lua bridge (Host wiring: the shared <c>LuaService</c>). Unset = every call is NotReady.</summary>
    internal ILua? Lua { get; set; }

    /// <summary>True while the player is in the world (Host wiring: <c>IClientState.IsWorldActive</c>). Our Lua reads
    /// <c>sceneline_data</c>, and mod Lua must never be the first caller of <c>Z.DataMgr.Get</c> (it caches a
    /// half-built object on failure — <c>Lua-Injection-from-CSharp.md</c>), so nothing runs before world entry.</summary>
    internal Func<bool>? WorldActive { get; set; }

    /// <summary>Game tables for refusal texts (Host wiring). Unset = English developer strings.</summary>
    internal IChatLinkTables? LinkTables { get; set; }

    public ChatPositionPreview GetSharePosition()
    {
        if (Gate() != ChatShareStatus.Ok) return new ChatPositionPreview();
        var packed = RunPreview(out var sceneName);
        if (!TryUnpackPreview(packed, sceneName, out var pos, out var can)) return new ChatPositionPreview();
        return new ChatPositionPreview
        {
            Available = true, Position = pos, CanShare = can,
            Reason = can ? null : GameText(MsgCannotShareHere, "Coordinates cannot be shared in this scene"),
        };
    }

    public ChatShareResult SharePosition(ChatTarget target, string? textBefore)
    {
        var gate = Gate();
        if (gate != ChatShareStatus.Ok) return new ChatShareResult { Status = gate };
        if (!TryShareChannel(target, out var channel, out var targetId))
            return new ChatShareResult { Status = ChatShareStatus.Unsupported, Message = $"cannot share to {target}" };
        var preview = GetSharePosition();
        if (!preview.Available) return new ChatShareResult { Status = ChatShareStatus.NotReady };
        if (!preview.CanShare) return Refused(MsgCannotShareHere, preview.Reason);

        var text = textBefore ?? string.Empty;
        if (text.Length > MaxShareText) text = text.Substring(0, MaxShareText);
        var call = ChatShareLua.CallShare(channel, targetId, text);
        if (!RunDefined(call, ChatShareLua.ShareSentKey, ChatShareLua.DefinePreview + ChatShareLua.DefineShare))
            return new ChatShareResult { Status = ChatShareStatus.Failed, Message = "share Lua did not start" };

        _shareChannel = ChannelOf(target);
        _shareDeadline = Stopwatch.GetTimestamp() + ShareTimeoutTicks;
        _shareNextPoll = 0;
        return new ChatShareResult { Status = ChatShareStatus.Ok };
    }

    public ChatShareResult TrackPosition(ChatPosition position)
    {
        if (position is null || position.SceneId <= 0)
            return new ChatShareResult { Status = ChatShareStatus.Unsupported, Message = "no scene id" };
        var gate = Gate();
        if (gate != ChatShareStatus.Ok) return new ChatShareResult { Status = gate };
        var call = ChatShareLua.CallTrack(position.SceneId, position.LineId, (position.X, position.Y, position.Z),
            position.MapLayerId, position.SceneGuid);
        var code = RunTrack(call);
        return code switch
        {
            0 => new ChatShareResult { Status = ChatShareStatus.Ok },
            2 => new ChatShareResult { Status = ChatShareStatus.Refused, Message = "That map is locked for this character" },
            3 => Refused(MsgUnreachable, null),
            _ => new ChatShareResult { Status = ChatShareStatus.Failed, Message = "track Lua threw" },
        };
    }

    // Logout (ClearSession): a pending share belongs to the old character.
    private void ResetSharePending() => _shareDeadline = 0;

    private ChatShareStatus Gate()
    {
        if (_mainThreadId == 0) return ChatShareStatus.NotReady;   // no Drain yet = framework not ticking
        if (Environment.CurrentManagedThreadId != _mainThreadId) return ChatShareStatus.WrongThread;
        if (Lua is not { Ready: true } || WorldActive?.Invoke() != true) return ChatShareStatus.NotReady;
        return ChatShareStatus.Ok;
    }

    private ChatShareResult Refused(int messageId, string? text) => new()
    {
        Status = ChatShareStatus.Refused, GameMessageId = messageId,
        Message = text ?? GameText(messageId, messageId == MsgUnreachable
            ? "Destination cannot be reached via auto-navigation" : "Coordinates cannot be shared in this scene"),
    };

    private string GameText(int messageId, string fallback)
    {
        var s = LinkTables?.GetMessageText(messageId);
        return string.IsNullOrEmpty(s) ? fallback : ChatRichText.Sanitize(s);
    }

    private static bool TryShareChannel(ChatTarget target, out int channel, out long targetId)
    {
        targetId = target is ChatTarget.WhisperTarget w ? w.TargetId : 0;
        channel = target switch
        {
            ChatTarget.WorldTarget => 1,
            ChatTarget.SayTarget => 2,       // ChitChatChannelType Scene = the game's "Current"
            ChatTarget.PartyTarget => 3,
            ChatTarget.GuildTarget => 4,
            ChatTarget.WhisperTarget => 5,
            _ => 0,
        };
        return channel != 0 && (channel != 5 || targetId != 0);
    }
}
