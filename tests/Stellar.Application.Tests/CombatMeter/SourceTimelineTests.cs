using Stellar.CombatMeter;
using Xunit;

namespace Stellar.Application.Tests.CombatMeter;

public sealed class SourceTimelineTests
{
    [Fact]
    public void Add_buckets_by_second_relative_to_start()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 600);
        t.Add(channel: TimelineChannel.Dealt, atMs: 0, startMs: 0, amount: 100);
        t.Add(TimelineChannel.Dealt, atMs: 500,  startMs: 0, amount: 50);   // same bucket 0
        t.Add(TimelineChannel.Dealt, atMs: 1500, startMs: 0, amount: 40);   // bucket 1
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.Equal(150, arr[0]);
        Assert.Equal(40,  arr[1]);
    }

    [Fact]
    public void Channels_are_independent()
    {
        var t = new SourceTimeline(1000, 600);
        t.Add(TimelineChannel.Dealt,   0, 0, 100);
        t.Add(TimelineChannel.Healing, 0, 0, 30);
        t.Add(TimelineChannel.Taken,   0, 0, 70);
        Assert.Equal(100, t.Freeze(TimelineChannel.Dealt)[0]);
        Assert.Equal(30,  t.Freeze(TimelineChannel.Healing)[0]);
        Assert.Equal(70,  t.Freeze(TimelineChannel.Taken)[0]);
    }

    [Fact]
    public void Coalesces_when_exceeding_maxBuckets()
    {
        var t = new SourceTimeline(bucketMs: 1000, maxBuckets: 4);
        // 8 seconds of 10/s -> 8 buckets > max 4, must coalesce to 2000ms buckets (4 buckets of 20).
        for (int sec = 0; sec < 8; sec++) t.Add(TimelineChannel.Dealt, sec * 1000, 0, 10);
        var arr = t.Freeze(TimelineChannel.Dealt);
        Assert.True(arr.Length <= 4);
        Assert.Equal(2000, t.BucketMs);           // widened once
        Assert.Equal(80, Sum(arr));               // total preserved
    }

    private static long Sum(long[] a) { long s = 0; foreach (var x in a) s += x; return s; }
}
