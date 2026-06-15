using Stellar.Application.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

public sealed class WhisperDedupTests
{
    [Fact]
    public void MarkSeen_FirstTimeTuple_ReturnsTrue()
    {
        var dedup = new WhisperDedup();

        Assert.True(dedup.MarkSeen(senderId: 100, msgId: 1, timestampTicks: 1000));
    }

    [Fact]
    public void MarkSeen_SameTupleTwice_SecondReturnsFalse()
    {
        var dedup = new WhisperDedup();
        dedup.MarkSeen(100, 1, 1000);

        Assert.False(dedup.MarkSeen(100, 1, 1000));
    }

    [Fact]
    public void MarkSeen_SameSenderAndMsgId_DifferentTimestamp_ReturnsTrue()
    {
        // Disambiguates the per-peer-conversation msg_id collision. Same sender
        // re-sends msg_id=1 to a different peer at a different time — that's a
        // genuinely new message.
        var dedup = new WhisperDedup();
        dedup.MarkSeen(100, 1, 1000);

        Assert.True(dedup.MarkSeen(100, 1, 2000));
    }

    [Fact]
    public void MarkSeen_DifferentSender_SameMsgIdAndTimestamp_ReturnsTrue()
    {
        // Two different players coincidentally have msg_id=1 at the same wire
        // timestamp — both are first-time observations.
        var dedup = new WhisperDedup();
        dedup.MarkSeen(100, 1, 1000);

        Assert.True(dedup.MarkSeen(200, 1, 1000));
    }

    [Fact]
    public void MarkSeen_MsgIdZero_AlwaysReturnsTrue()
    {
        // msgId=0 means "unknown"; degrades to no-dedup so partial parses don't
        // get silently dropped.
        var dedup = new WhisperDedup();
        dedup.MarkSeen(100, 0, 1000);

        Assert.True(dedup.MarkSeen(100, 0, 1000));
        Assert.True(dedup.MarkSeen(100, 0, 1000));
    }

    [Fact]
    public void MarkSeen_SenderIdZero_AlwaysReturnsTrue()
    {
        // senderId=0 means we couldn't extract a sender from the payload —
        // treat conservatively as fresh so we don't lose messages with partial
        // parses.
        var dedup = new WhisperDedup();
        dedup.MarkSeen(0, 1, 1000);

        Assert.True(dedup.MarkSeen(0, 1, 1000));
        Assert.True(dedup.MarkSeen(0, 1, 1000));
    }

    [Fact]
    public void MarkSeen_FifoEviction_FirstInsertedIsPushedOut_AtCapacityPlusOne()
    {
        // Insert 1024 entries (fills cache), then one more — this evicts the
        // FIRST entry inserted, since FIFO eviction kicks in only after Count >
        // MaxCacheSize. Re-marking the evicted key must return true (fresh
        // observation) because it's no longer in the seen-set.
        var dedup = new WhisperDedup();
        const int cap = WhisperDedup.MaxCacheSize;

        for (int i = 0; i < cap; i++)
        {
            Assert.True(dedup.MarkSeen(senderId: 1, msgId: i + 1, timestampTicks: i + 1));
        }
        // One more push to trigger eviction of the first entry.
        Assert.True(dedup.MarkSeen(senderId: 1, msgId: cap + 1, timestampTicks: cap + 1));

        // The first entry has been evicted; re-marking it returns true (fresh
        // observation) instead of false (still seen).
        Assert.True(dedup.MarkSeen(senderId: 1, msgId: 1, timestampTicks: 1));
    }

    [Fact]
    public void MarkSeen_FifoEviction_StillRetainsRecentEntries()
    {
        // After eviction, the more-recently-added entries must still dedup
        // correctly. Insert cap entries, push one over to evict (1,1), then
        // verify a mid-window entry still returns false on re-mark.
        var dedup = new WhisperDedup();
        const int cap = WhisperDedup.MaxCacheSize;

        for (int i = 0; i < cap; i++)
        {
            dedup.MarkSeen(senderId: 1, msgId: i + 1, timestampTicks: i + 1);
        }
        dedup.MarkSeen(senderId: 1, msgId: cap + 1, timestampTicks: cap + 1);

        // Mid-window entry — well within the surviving window — must still be
        // recognized as seen.
        Assert.False(dedup.MarkSeen(senderId: 1, msgId: cap / 2, timestampTicks: cap / 2));
    }

    [Fact]
    public void Reset_ClearsAllSeenEntries()
    {
        var dedup = new WhisperDedup();
        dedup.MarkSeen(100, 1, 1000);
        dedup.MarkSeen(200, 2, 2000);

        dedup.Reset();

        Assert.True(dedup.MarkSeen(100, 1, 1000));
        Assert.True(dedup.MarkSeen(200, 2, 2000));
    }
}
