using System;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Application.Abstractions;
using Stellar.Application.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Receive-side wire-tap consumers for <see cref="PandaChatProbe"/>: the
/// ChitChatNtf method=1 / method=3 envelope handlers plus the all-Returns
/// consumer that feeds the history processor on <c>.History.cs</c>. The
/// HarmonyX postfix dispatch lives on <c>.Receive.Dispatch.cs</c>; candidate
/// resolution on <c>.Receive.Candidates.cs</c>; concrete-type resolution on
/// <c>.Receive.Resolution.cs</c>; payload extraction on
/// <c>.Receive.Extraction.cs</c>; stub-call projection on
/// <c>.Receive.Chat.cs</c>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ----------------- receive-path one-shot diagnostic flags -----------------
    // Write-once on the I/O thread, read-only after.
    //   _firstChitChatLogged           — first stub-call receive surfaces concrete type
    //   _firstChatPacketSeenLogged     — first ChitChatNtf envelope dispatched
    //   _firstChatParsedLogged         — first successfully parsed ChitChatNtf method=1
    //   _chatParseFailLogged           — first parse failure on method=1
    //   _firstMethod3ParseFailLogged   — first parse failure on method=3
    //   _firstMethod3ReceivedLogged    — first successful parse on method=3
    private bool _firstChitChatLogged;
    private bool _firstChatPacketSeenLogged;
    private bool _firstChatParsedLogged;
    private bool _chatParseFailLogged;
    private bool _firstMethod3ParseFailLogged;
    private bool _firstMethod3ReceivedLogged;

    /// <summary>
    /// Wire-tap consumer for <c>ChitChatNtf.NotifyNewestChitChatMsgs</c>
    /// (serviceUuid=164931432, method=1). Runs on the network I/O thread.
    /// <c>env.Payload</c> arrives already zstd-decompressed.
    /// </summary>
    internal void OnChatNotifyEnvelope(WireEnvelope env)
    {
        try
        {
            CacheChatTcpClient(env, label: null);

            if (!_firstChatPacketSeenLogged)
            {
                _firstChatPacketSeenLogged = true;
                _log.Info($"[Chat] wire: first chat Notify observed serviceUuid={env.ServiceUuid} methodId={env.MethodId} payloadLen={env.Payload.Length}");
            }

            var payloadSpan = env.Payload.Span;
            var requestBytes = WireProtocol.UnwrapNotifyEnvelope(payloadSpan);
            if (!WireProtocol.TryParseChitChatNotify(requestBytes, out var chatMsg, out var notifyMsgId) || chatMsg is null)
            {
                if (!_chatParseFailLogged)
                {
                    _chatParseFailLogged = true;
                    _log.Info($"[Chat] parse failed for ChitChatNtf NotifyNewestChitChatMsgs: serviceUuid={env.ServiceUuid} payloadLen={payloadSpan.Length} requestLen={requestBytes.Length}");
                }
                return;
            }

            // Dedup against the history path. If a history Return for the same
            // (sender, msg_id, timestamp) arrived first, skip — otherwise we'd
            // render this message twice in plugin UIs (once with proper channel
            // from Notify, once with Unknown channel from history). The
            // timestamp tick is the third disambiguator: msg_id is per-peer-
            // conversation, so a sender's own msg_id=1 across two different
            // peer whisper conversations would otherwise collide.
            if (!MarkSeen(chatMsg.SenderId, notifyMsgId, chatMsg.Timestamp.Ticks)) return;

            RecordNotifyChannel(chatMsg);
            OnMessageReceived?.Invoke(chatMsg);
            ReportFirstParsedNotify(chatMsg);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] OnChatNotifyEnvelope threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wire-tap consumer for <c>ChitChatNtf</c> service uuid + <c>method=3</c>.
    /// Empirically discovered 2026-05-22 from a live trace:
    /// <c>[WireTap] first Notify svc=164931432 method=3 payloadLen=53</c>.
    /// Method=1 only ever delivered chat from the user's own alt (Revette);
    /// stranger/cross-account whispers were arriving on this path and being
    /// silently dropped by the unregistered-method filter.
    ///
    /// Strategy: hex-dump the first 5 payloads in parallel with parse attempts
    /// so even if the parser silently produces wrong output we still have the
    /// raw bytes for forensic analysis. Then try
    /// <see cref="WireProtocol.TryParseChitChatNotify"/> on the assumption that
    /// method=3 has the same shape as method=1 (most service catalogs reuse
    /// the same request proto across notification methods that differ only by
    /// routing channel). If parse succeeds, dispatch through the same dedup +
    /// per-sender-channel-cache + <see cref="OnMessageReceived"/> code path
    /// used by method=1. If parse fails, emit a one-shot diagnostic so the
    /// hex dump is the only evidence we have on the next iteration.
    /// </summary>
    internal void OnChatMethod3Envelope(WireEnvelope env)
    {
        try
        {
            // Whispers from strangers may be the first inbound chat traffic on
            // this connection if the user hasn't received any alt traffic yet.
            CacheChatTcpClient(env, label: " (via method=3 path)");

            var payloadSpan = env.Payload.Span;

            // Diagnostics mode: hex-dump the first 5 method=3 payloads
            // regardless of parse outcome. Useful for forensic analysis if the
            // parser silently produces wrong output. Entry point gates on
            // STELLAR_DIAGNOSTICS=1 + remaining-dump counter; off-mode hot
            // path pays a single static-field read. See PandaChatProbe.Diagnostics.cs.
            DiagMethod3Hex(payloadSpan);

            // Attempt parse on the assumption method=3 shares the method=1
            // envelope/request shape.
            var requestBytes = WireProtocol.UnwrapNotifyEnvelope(payloadSpan);
            if (!WireProtocol.TryParseChitChatNotify(requestBytes, out var chatMsg, out var notifyMsgId) || chatMsg is null)
            {
                if (!_firstMethod3ParseFailLogged)
                {
                    _firstMethod3ParseFailLogged = true;
                    _log.Info($"[Chat method=3] parse failed (assuming method=1 shape): serviceUuid={env.ServiceUuid} payloadLen={payloadSpan.Length} requestLen={requestBytes.Length}");
                }
                return;
            }

            // Same dedup as method=1 — cross-path duplicates against
            // GetChipChatRecordsReply (history) are guarded by the shared
            // (senderId, msgId, timestamp) seen-set. Timestamp is required
            // because msg_id is per-peer-conversation, not globally unique.
            if (!MarkSeen(chatMsg.SenderId, notifyMsgId, chatMsg.Timestamp.Ticks)) return;

            RecordNotifyChannel(chatMsg);
            OnMessageReceived?.Invoke(chatMsg);
            ReportFirstParsedMethod3(chatMsg);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] OnChatMethod3Envelope threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Wire-tap consumer for ALL <c>Return</c> envelopes. Returns carry no
    /// service_uuid/method_id on the wire — we attempt-parse every Return as
    /// <c>GetChipChatRecords_Ret</c>. Non-chat Returns produce zero messages
    /// (the protobuf scan finds no field 5 with ChitChatMsg-shaped entries),
    /// so dispatch is effectively self-filtering. Call_id correlation
    /// (populated by the outbound Send prefix) feeds a one-shot diagnostic so
    /// we can confirm our send-side hook is firing.
    /// </summary>
    internal void OnAnyReturnEnvelope(WireEnvelope env)
    {
        try
        {
            uint callId = env.CallId;
            bool correlated = _pendingChitChatCallIds.TryRemove(callId, out _);
            ProcessChitChatReturnPayload(env.Payload.Span, callId, correlated);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] OnAnyReturnEnvelope threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Lazily cache the chat <c>ZTcpClient</c> instance from the first inbound
    /// envelope that carries one. <see cref="_chatTcpClient"/> is the outbound
    /// send target for <see cref="IChat.Send"/>. Single-set semantics under
    /// <see cref="_chatTcpClientLock"/>; subsequent calls are no-ops.
    /// </summary>
    /// <param name="label">Optional suffix appended to the boot log (e.g.
    /// <c>" (via method=3 path)"</c> distinguishes which notify path captured
    /// the client). Null = no suffix.</param>
    private void CacheChatTcpClient(WireEnvelope env, string? label)
    {
        if (_chatTcpClient is not null || env.Connection is null) return;
        lock (_chatTcpClientLock)
        {
            if (_chatTcpClient is null)
            {
                _chatTcpClient = env.Connection;
                _log.Info($"[ChatProbe] cached chat ZTcpClient instance ({env.Connection.GetType().FullName}) for outbound send{label ?? string.Empty}");
            }
        }
    }

    /// <summary>
    /// Record (<paramref name="chatMsg"/>.SenderId → <paramref name="chatMsg"/>.Channel)
    /// in <see cref="_senderLastChannel"/> so the history-batch refinement
    /// heuristic on <c>.History.cs</c> can attribute the correct channel to
    /// any cross-path duplicate it dedups against. SenderId=0 is the
    /// system/broadcast slot and is intentionally skipped.
    /// </summary>
    private void RecordNotifyChannel(ChatMessage chatMsg)
    {
        if (chatMsg.SenderId == 0) return;
        _senderLastChannel[chatMsg.SenderId] = chatMsg.Channel;
    }

    /// <summary>
    /// One-shot boot diagnostic for the method=1 receive path. The first parsed
    /// Notify logs the channel/sender/text triple (text truncated to 80 chars)
    /// so the wire path is observable at boot; subsequent messages fall
    /// through to <see cref="DiagChatReceived"/> which gates on
    /// STELLAR_DIAGNOSTICS=1.
    /// </summary>
    private void ReportFirstParsedNotify(ChatMessage chatMsg)
    {
        if (!_firstChatParsedLogged)
        {
            _firstChatParsedLogged = true;
            var dbgText = chatMsg.Text ?? string.Empty;
            if (dbgText.Length > 80) dbgText = dbgText.Substring(0, 80);
            _log.Info($"[Chat] first received: channel={chatMsg.Channel} sender='{chatMsg.SenderName}' (id={chatMsg.SenderId}) text='{dbgText}'");
        }
        else
        {
            DiagChatReceived(chatMsg);
        }
    }

    /// <summary>
    /// One-shot boot diagnostic for the method=3 receive path — confirms
    /// method=3 is delivering parseable chat traffic. Subsequent messages
    /// travel through <see cref="DiagChatMethod3Received"/> which gates on
    /// STELLAR_DIAGNOSTICS=1.
    /// </summary>
    private void ReportFirstParsedMethod3(ChatMessage chatMsg)
    {
        if (!_firstMethod3ReceivedLogged)
        {
            _firstMethod3ReceivedLogged = true;
            var dbgText = chatMsg.Text ?? string.Empty;
            if (dbgText.Length > 80) dbgText = dbgText.Substring(0, 80);
            _log.Info($"[Chat method=3] first received: channel={chatMsg.Channel} sender='{chatMsg.SenderName}' (id={chatMsg.SenderId}) text='{dbgText}'");
        }
        else
        {
            DiagChatMethod3Received(chatMsg);
        }
    }
}
