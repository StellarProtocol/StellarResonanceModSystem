// tests/Stellar.Application.Tests/Theme/SlotCacheRefreshTests.cs
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// Pins the consumer-side cache-refresh decision (ThemeEditorBody.SlotAt) that the owner review of 64ddcb4
// found untested: a Relabel bumps Revision without ever changing SlotCount, so keying the cache on count
// alone misses it. Reverting ThemeEditorBody.Describe.cs's SlotAt back to a count-only key must fail this
// suite's RevisionChangeWithEqualCount_Refreshes case.
public sealed class SlotCacheRefreshTests
{
    [Fact]
    public void SameCountAndRevision_DoesNotRefresh()
        => Assert.False(SlotCacheRefresh.ShouldRefresh(currentCount: 5, currentRevision: 3, cachedCount: 5, cachedRevision: 3));

    [Fact]
    public void CountChange_Refreshes()
        => Assert.True(SlotCacheRefresh.ShouldRefresh(currentCount: 6, currentRevision: 3, cachedCount: 5, cachedRevision: 3));

    // The case the stale-cache bug was actually about: a Relabel never moves SlotCount.
    [Fact]
    public void RevisionChangeWithEqualCount_Refreshes()
        => Assert.True(SlotCacheRefresh.ShouldRefresh(currentCount: 5, currentRevision: 4, cachedCount: 5, cachedRevision: 3));

    [Fact]
    public void BothChange_Refreshes()
        => Assert.True(SlotCacheRefresh.ShouldRefresh(currentCount: 6, currentRevision: 4, cachedCount: 5, cachedRevision: 3));
}
