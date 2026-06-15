using System;
using System.Collections;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Stub-call chat projection for <see cref="PandaChatProbe"/>. Decomposes a
/// <c>Chat.Pb.{Private,Channel,World}ChatReceive</c> wrapper into individual
/// <see cref="ChatMessage"/> records and fires <see cref="OnMessageReceived"/>
/// per inner message. Driven by the postfix dispatcher on
/// <c>.Receive.Dispatch.cs</c>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Project a Chat.Pb.{Private,Channel,World}ChatReceive into one or more
    /// <see cref="ChatMessage"/> records. Each receive wrapper holds
    /// <c>msg_list : repeated {Private,Channel,World}Message</c>; each of those
    /// holds its own <c>msg_list : repeated ChatMessage</c>. We iterate both
    /// and fire <see cref="OnMessageReceived"/> per inner ChatMessage.
    /// </summary>
    private void OnChatReceiveMessage(object stubCall, object msg, string concreteType)
    {
        var channel = MapReceiveType(concreteType);

        // One-shot diagnostic: surface the first stub-call receive so the
        // stub-routed path is observable at boot, then go silent.
        if (!_firstChitChatLogged)
        {
            _firstChitChatLogged = true;
            var uuidEarly = ChatPropertyReader.TryReadProp(stubCall, _uuidProperty);
            var methodIdEarly = ChatPropertyReader.TryReadProp(stubCall, _methodIdProperty);
            _log.Info($"[Chat] first stubcall received: type={concreteType} channel={channel} uuid={uuidEarly} methodId={methodIdEarly}");
        }
        else
        {
            DiagStubCallReceived(stubCall, concreteType, channel);
        }

        // The wrapper's msg_list is the only protobuf field; managed reflection
        // exposes it under the PascalCase generated name (MsgList).
        var outerType = msg.GetType();
        var outerListMember = ChatPropertyReader.ResolveListMember(outerType);
        var outerList = ChatPropertyReader.ReadList(msg, outerListMember);
        if (outerList is null)
        {
            return;
        }

        IterateChatMessages(outerList, channel);
    }

    /// <summary>
    /// Walk the two-level msg_list structure of a Chat.Pb.*ChatReceive
    /// wrapper: outer = repeated per-client wrapper (<c>PrivateMessage</c>,
    /// <c>ChannelMessage</c>, <c>WorldMessage</c>), inner = the
    /// per-conversation <c>repeated ChatMessage</c>. Projects each leaf
    /// <c>ChatMessage</c> through <see cref="OnMessageReceived"/>. v1 sets
    /// <c>SenderName = from_client_id</c>; BaseInfo bytes parsing comes later.
    /// </summary>
    private void IterateChatMessages(IEnumerable outerList, ChatChannel channel)
    {
        foreach (var perClient in outerList)
        {
            if (perClient is null) continue;
            var perClientType = perClient.GetType();
            var clientId = ChatPropertyReader.ReadStringMember(perClient, perClientType, "ClientId", "client_id") ?? string.Empty;
            // ChannelMessage also has channel_id; we don't currently use it but read
            // defensively in case future logic wants to disambiguate Party/Guild/Say.
            var innerListMember = ChatPropertyReader.ResolveListMember(perClientType);
            var innerList = ChatPropertyReader.ReadList(perClient, innerListMember);
            if (innerList is null) continue;

            foreach (var inner in innerList)
            {
                if (inner is null) continue;
                var innerType = inner.GetType();
                var fromClientId = ChatPropertyReader.ReadStringMember(inner, innerType, "FromClientId", "from_client_id") ?? clientId;
                var text = ChatPropertyReader.ReadStringMember(inner, innerType, "Msg", "msg", "Text", "text") ?? string.Empty;
                var sendTime = ChatPropertyReader.ReadInt64Member(inner, innerType, "SendTime", "send_time");

                var projected = new ChatMessage(
                    Channel:    channel,
                    SenderName: fromClientId,
                    SenderId:   0L,
                    Text:       text,
                    Timestamp:  sendTime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(sendTime).UtcDateTime
                        : DateTime.UtcNow,
                    Type:       ChatMessageType.Unknown,
                    RawPayload: ReadOnlyMemory<byte>.Empty,
                    Sender:     null,
                    MsgId:      0L);

                OnMessageReceived?.Invoke(projected);
            }
        }
    }
}
