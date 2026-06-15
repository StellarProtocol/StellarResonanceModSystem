using System.Collections.Generic;

namespace Stellar.Application.Wire;

/// <summary>
/// Cross-path dedup for ChitChat messages. The chat history fetch at login
/// returns recent messages — but those same messages also flow as live
/// Notifies, so any overlap produces visible duplicates in the plugin UI.
///
/// <para>
/// Empirically (live log evidence 2026-05-22) <c>ChitChatMsg.msg_id</c> is
/// NOT globally unique despite the proto comment claiming so — for whispers
/// the SEA server returns per-conversation msg_id sequences starting at small
/// numbers (1, 2, 3...) per <c>(self, peer)</c> pair. Widening to
/// <c>(senderId, msgId)</c> fixed the history-batch collision between peers,
/// but the SAME local sender reuses msg_id sequences across different peer
/// conversations. Including the wire <c>send_time</c> (projected to
/// <see cref="ChatMessage.Timestamp.Ticks"/>) as a third disambiguator covers
/// that case: same <c>(senderId, msgId)</c> reused across two conversations
/// will have different timestamps because the messages were sent at different
/// times.
/// </para>
///
/// <para>
/// FIFO eviction keeps the working set bounded at <see cref="MaxCacheSize"/>
/// entries (1024) so a long session can't grow the dedup table unbounded.
/// Single-lock dispatch is fine — chat throughput is low (≤ a few messages
/// per second peak).
/// </para>
/// </summary>
internal sealed class WhisperDedup
{
    public const int MaxCacheSize = 1024;

    private readonly object _lock = new();
    private readonly HashSet<(long SenderId, long MsgId, long TimestampTicks)> _seen = new();
    private readonly Queue<(long SenderId, long MsgId, long TimestampTicks)> _fifo = new();

    /// <summary>
    /// Returns true if <paramref name="msgId"/> from <paramref name="senderId"/>
    /// at <paramref name="timestampTicks"/> is being seen for the first time;
    /// false if it was already dispatched on another path. Caller should skip
    /// dispatch on false.
    ///
    /// <para>
    /// A <paramref name="msgId"/> of zero means "unknown" — always allow
    /// dispatch (degrades gracefully to pre-dedup behavior). A
    /// <paramref name="senderId"/> of zero means we couldn't extract a sender
    /// from the payload; treat conservatively as fresh so we don't lose
    /// messages with partial parses.
    /// </para>
    /// </summary>
    public bool MarkSeen(long senderId, long msgId, long timestampTicks)
    {
        if (msgId == 0 || senderId == 0) return true;
        var key = (senderId, msgId, timestampTicks);
        lock (_lock)
        {
            if (!_seen.Add(key)) return false;
            _fifo.Enqueue(key);
            while (_fifo.Count > MaxCacheSize)
            {
                var evict = _fifo.Dequeue();
                _seen.Remove(evict);
            }
            return true;
        }
    }

    /// <summary>Clear all dedup state. Test-only — production never resets mid-session.</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _seen.Clear();
            _fifo.Clear();
        }
    }
}
