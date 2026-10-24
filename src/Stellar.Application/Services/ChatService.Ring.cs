using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Storage behind <see cref="Stellar.Abstractions.Services.IChat.RecentMessages"/>: TWO bounded rings merged back into
/// one arrival-ordered list. The game's client-generated system notices ("Obtained Guild Fund×40", item caps — the ones
/// <see cref="SystemNoticeService"/> publishes, recognisable by a non-null <see cref="ChatMessage.SystemLabel"/>) can fire
/// dozens of times a minute while farming; in the old single 500-entry ring they evicted real World/Guild chat (and the
/// login history) long before anyone scrolled back to it. They now live in their own smaller ring, so neither kind can
/// push the other out. Server-sent System lines (TopNotice / Play, SystemLabel null) are wire chat and stay in the main
/// ring. Every entry carries a global arrival number and the snapshot merges the two rings on it, so a consumer still
/// sees ONE list in pure arrival order with the notices interleaved exactly where they arrived (no sort, no timestamp
/// tie-breaks — the contract of the single ring is unchanged). The ring members run under <c>_ringLock</c>.
/// </summary>
internal sealed partial class ChatService
{
    // Bumped from 100 to 500 to hold the login-time history fetch (typically
    // 4 batches × ~30 messages = 120) without evicting older batches before
    // the user can scroll back to read them.
    private const int RingCapacity = 500;
    // Client notices: a separate, smaller budget (they are one-liners nobody scrolls far back for).
    private const int NoticeRingCapacity = 100;

    private readonly Queue<(long Seq, ChatMessage Msg)> _ring = new(RingCapacity);
    private readonly Queue<(long Seq, ChatMessage Msg)> _noticeRing = new(NoticeRingCapacity);
    private long _arrivalSeq;   // global arrival number across both rings (merge key)

    private static bool IsClientNotice(ChatMessage msg) => msg.SystemLabel != null;

    /// <summary>Logout (Host OnLogout, main thread, AFTER <c>IClientState.Logout</c> subscribers ran — a plugin cutting
    /// its view at logout still sees the old tail first): empty both rings and the not-yet-drained queue, forget the
    /// per-channel send cooldowns, and reset the probe's per-session receive state (the history/live dedup — see
    /// <see cref="IChatProbe.ResetSession"/>). <c>RecentMessages</c> then really is "since the last login" as documented.
    /// Consumers holding a ring entry as a cut point find it gone, which already means "everything left is newer".</summary>
    internal void ClearSession()
    {
        while (_wireQueue.TryDequeue(out _)) { }
        lock (_ringLock)
        {
            _ring.Clear();
            _noticeRing.Clear();
            Interlocked.Increment(ref _ringVersion);
        }
        lock (_cooldownLock) _cooldowns.Clear();
        _probe?.ResetSession();
    }

    // ── Consumers (lazy background work) ──
    // A plugin "uses chat" while it has a MessageReceived subscriber or has read RecentMessages within this window.
    // SystemNoticeService only wraps/polls the game's notice VM while that is true (no consumer → nobody would see the
    // notices). Environment.TickCount64 = ms, any thread, cheap enough for the per-frame RecentMessages fast path.
    private const long ConsumerIdleMs = 30_000;
    private long _lastRecentReadMs = long.MinValue / 2;   // "never" (no overflow in the subtraction below)

    private void NoteRecentRead() => Volatile.Write(ref _lastRecentReadMs, Environment.TickCount64);

    /// <summary>True while some plugin consumes chat: a <c>MessageReceived</c> subscriber, or a
    /// <c>RecentMessages</c> read in the last <see cref="ConsumerIdleMs"/>.</summary>
    internal bool HasConsumers()
        => _handlers != null || Environment.TickCount64 - Volatile.Read(ref _lastRecentReadMs) < ConsumerIdleMs;

    // Under _ringLock (Drain).
    private void AppendToRing(ChatMessage msg)
    {
        var notice = IsClientNotice(msg);
        var ring = notice ? _noticeRing : _ring;
        if (ring.Count >= (notice ? NoticeRingCapacity : RingCapacity)) ring.Dequeue();
        ring.Enqueue((++_arrivalSeq, msg));
    }

    // Under _ringLock. Two-way merge on the arrival number (both rings are already in arrival order). The enumerators
    // are plain locals, NOT `using var`: Queue's enumerator is a struct and a using-declared local is readonly, so
    // MoveNext would advance a defensive copy and never move.
    private ChatMessage[] MergedRingSnapshot()
    {
        var all = new ChatMessage[_ring.Count + _noticeRing.Count];
        var a = _ring.GetEnumerator();
        var b = _noticeRing.GetEnumerator();
        bool hasA = a.MoveNext(), hasB = b.MoveNext();
        var i = 0;
        while (hasA || hasB)
        {
            if (hasB && (!hasA || b.Current.Seq < a.Current.Seq)) { all[i++] = b.Current.Msg; hasB = b.MoveNext(); }
            else { all[i++] = a.Current.Msg; hasA = a.MoveNext(); }
        }
        return all;
    }
}
