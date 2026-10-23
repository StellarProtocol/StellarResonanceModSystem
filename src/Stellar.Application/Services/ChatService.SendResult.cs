using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain;

namespace Stellar.Application.Services;

/// <summary>
/// Server-driven send cooldown (<see cref="Stellar.Abstractions.Services.IChat.TryGetSendCooldown"/>) and the
/// <see cref="Stellar.Abstractions.Services.IChat.SendResult"/> event. The probe hands us each send reply on the
/// wire thread: the per-channel slot updates immediately (so a query right after the reply is current) and the
/// event is queued for <see cref="Drain"/> on the main thread.
/// </summary>
internal sealed partial class ChatService
{
    // Cooldown end per channel: server Unix ms (primary, compared against the interpolated server clock) plus a
    // local Stopwatch deadline taken at reply time (fallback while the server clock isn't synced yet, 0 = none).
    private readonly record struct CooldownSlot(long EndServerMs, long LocalDeadlineTicks);

    private readonly Dictionary<ChatChannel, CooldownSlot> _cooldowns = new();
    private readonly object _cooldownLock = new();
    private readonly ConcurrentQueue<ChatSendResult> _sendResults = new();
    private Action<ChatSendResult>? _sendResultHandlers;

    /// <summary>Framework interpolated server clock (Unix ms, 0 = not yet synced) — <c>CombatSnapshot.ServerNowMs</c>.
    /// Set by Host wiring.</summary>
    internal Func<long>? ServerNowMs { get; set; }

    public event Action<ChatSendResult> SendResult
    {
        add { lock (_handlersLock) _sendResultHandlers += value; }
        remove { lock (_handlersLock) _sendResultHandlers -= value; }
    }

    public bool TryGetSendCooldown(ChatChannel channel, out double remainingSeconds)
    {
        remainingSeconds = 0;
        CooldownSlot slot;
        lock (_cooldownLock)
        {
            if (!_cooldowns.TryGetValue(channel, out slot)) return false;
        }
        var nowMs = ServerNowMs?.Invoke() ?? 0L;
        if (nowMs > 0) remainingSeconds = (slot.EndServerMs - nowMs) / 1000.0;
        else if (slot.LocalDeadlineTicks != 0)
            remainingSeconds = (slot.LocalDeadlineTicks - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
        if (remainingSeconds <= 0) { remainingSeconds = 0; return false; }
        return true;
    }

    // Wire thread (probe). An accepted send with no cooldown (Party/Guild) clears the slot; a refused send without a
    // cooldown end (rare) leaves the last known one. The local deadline measures the same span from the reply's own
    // server stamp when the framework clock isn't synced yet.
    private void OnWireSendResult(ChatSendResult result)
    {
        if (result.CooldownEndUtc is { } end)
        {
            long endMs = new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            var nowMs = ServerNowMs?.Invoke() ?? 0L;
            if (nowMs <= 0 && result.ServerTimeUtc is { } stamp)
                nowMs = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
            long deadline = nowMs > 0
                ? Stopwatch.GetTimestamp() + (long)((endMs - nowMs) / 1000.0 * Stopwatch.Frequency)
                : 0L;
            lock (_cooldownLock) _cooldowns[result.Channel] = new CooldownSlot(endMs, deadline);
        }
        else if (result.Ok)
        {
            lock (_cooldownLock) _cooldowns.Remove(result.Channel);
        }
        _sendResults.Enqueue(result);
    }

    // Main thread, from Drain.
    private void DrainSendResults()
    {
        while (_sendResults.TryDequeue(out var r))
        {
            var handlers = _sendResultHandlers;
            if (handlers is null) continue;
            foreach (Action<ChatSendResult> h in handlers.GetInvocationList())
            {
                try { h(r); }
                catch (Exception ex) { _log.Warning($"[Chat] SendResult subscriber threw: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
    }
}
