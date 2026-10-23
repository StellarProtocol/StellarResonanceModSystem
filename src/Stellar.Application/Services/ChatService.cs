using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

internal sealed partial class ChatService : IChat
{
    // Bumped from 100 to 500 to hold the login-time history fetch (typically
    // 4 batches × ~30 messages = 120) without evicting older batches before
    // the user can scroll back to read them.
    private const int RingCapacity = 500;

    private readonly ConcurrentQueue<ChatMessage> _wireQueue = new();
    private readonly Queue<ChatMessage>           _ring      = new(RingCapacity);
    private readonly object                       _ringLock  = new();
    private readonly IPluginLog                   _log;

    private IChatProbe?               _probe;
    private ChatMessage[]             _snapshot = Array.Empty<ChatMessage>();
    private int                       _snapshotVersion;
    private int                       _ringVersion;

    private Action<ChatMessage>[]?    _handlers;
    private readonly object           _handlersLock = new();

    public ChatService(IPluginLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void AttachProbe(IChatProbe probe)
    {
        if (probe is null) throw new ArgumentNullException(nameof(probe));
        _probe = probe;
        probe.OnMessageReceived = OnWireMessage;
    }

    public IReadOnlyList<ChatMessage> RecentMessages
    {
        get
        {
            if (Volatile.Read(ref _ringVersion) == _snapshotVersion) return _snapshot;
            lock (_ringLock)
            {
                var cur = _ringVersion;
                if (cur != _snapshotVersion)
                {
                    _snapshot = _ring.ToArray();
                    _snapshotVersion = cur;
                }
                return _snapshot;
            }
        }
    }

    public event Action<ChatMessage> MessageReceived
    {
        add
        {
            if (value is null) return;
            lock (_handlersLock)
            {
                if (_handlers is null)
                {
                    _handlers = new[] { value };
                    return;
                }
                var next = new Action<ChatMessage>[_handlers.Length + 1];
                Array.Copy(_handlers, next, _handlers.Length);
                next[_handlers.Length] = value;
                _handlers = next;
            }
        }
        remove
        {
            if (value is null) return;
            lock (_handlersLock)
            {
                if (_handlers is null) return;
                var idx = Array.IndexOf(_handlers, value);
                if (idx < 0) return;
                if (_handlers.Length == 1)
                {
                    _handlers = null;
                    return;
                }
                var next = new Action<ChatMessage>[_handlers.Length - 1];
                Array.Copy(_handlers, 0, next, 0, idx);
                Array.Copy(_handlers, idx + 1, next, idx, _handlers.Length - idx - 1);
                _handlers = next;
            }
        }
    }

    public void Send(ChatTarget target, string text)
    {
        if (_probe is null)
        {
            _log.Info("[Chat] send failed: probe not attached");
            return;
        }
        try
        {
            if (!_probe.TrySend(target, text, out var reason))
            {
                _log.Info($"[Chat] send failed: {reason ?? "(no reason given)"}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[Chat] send threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void SendSticker(ChatTarget target, int stickerConfigId)
    {
        if (_probe is null)
        {
            _log.Info("[Chat] sticker send failed: probe not attached");
            return;
        }
        try
        {
            if (!_probe.TrySendSticker(target, stickerConfigId, out var reason))
            {
                _log.Info($"[Chat] sticker send failed: {reason ?? "(no reason given)"}");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[Chat] sticker send threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cadence: called once per Game.Update postfix on the main thread. Drains wire queue, updates ring, fires MessageReceived.
    public void Drain()
    {
        while (_wireQueue.TryDequeue(out var queued))
        {
            var msg = ResolveWhisperPeer(queued);
            lock (_ringLock)
            {
                if (_ring.Count >= RingCapacity) _ring.Dequeue();
                _ring.Enqueue(msg);
                Interlocked.Increment(ref _ringVersion);
            }
            FireReceived(msg);
        }
    }

    private void FireReceived(ChatMessage msg)
    {
        var snapshot = _handlers;
        DiagDispatched(msg, snapshot);
        if (snapshot is null) return;
        for (var i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i](msg); }
            catch (Exception ex)
            {
                _log.Warning($"[Chat] subscriber threw: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // Called by the probe on the wire thread.
    private void OnWireMessage(ChatMessage msg) => _wireQueue.Enqueue(msg);

    /// <summary>Injects a framework-sourced message (client-generated system notices — they never reach the wire)
    /// into the same queue the probe feeds, so it rides the ring + <see cref="MessageReceived"/> on the next
    /// <see cref="Drain"/>. Any thread.</summary>
    internal void PublishLocal(ChatMessage msg) => _wireQueue.Enqueue(msg);

    /// <summary>Local player's charId source (<c>IPlayerIdentity.CharId</c>, 0 = unknown). Set by Host wiring;
    /// read on the main thread in <see cref="Drain"/> only.</summary>
    internal Func<long>? SelfCharId { get; set; }

    // Whisper peer rule (the game's own, chat_main_vm.lua:796-807): the other party is the sender, or the
    // msg_info target when the sender is self. Done here on the main thread (not in the wire-thread probe) because
    // "self" comes from player state. A record with NO target can't be our own echo (an outgoing whisper always
    // names its recipient), so its sender is the peer even before self is known. Already-tagged rows (private
    // history batches carry the request's peer) and non-whispers pass through untouched — zero allocation.
    private ChatMessage ResolveWhisperPeer(ChatMessage msg)
    {
        if (msg.Channel != ChatChannel.Whisper || msg.PeerId != 0) return msg;
        if (msg.TargetId == 0) return msg.SenderId != 0 ? msg with { PeerId = msg.SenderId } : msg;
        var self = SelfCharId?.Invoke() ?? 0L;
        if (self == 0) return msg;   // undecidable — plugin compares SenderId/TargetId itself (documented on PeerId)
        var peer = msg.SenderId == self ? msg.TargetId : msg.SenderId;
        return peer != 0 ? msg with { PeerId = peer } : msg;
    }
}
