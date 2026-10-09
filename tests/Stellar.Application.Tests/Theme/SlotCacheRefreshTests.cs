// tests/Stellar.Application.Tests/Theme/SlotCacheRefreshTests.cs
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Theme;

// Pins the pure refresh-decision helper (extracted from ThemeEditorBody.SlotAt, which the owner review of
// 64ddcb4 found untested) that was the actual root cause: a Relabel bumps Revision without ever changing
// SlotCount, so keying the cache on count alone misses it. This suite pins SlotCacheRefresh.ShouldRefresh
// itself — reverting ITS body back to a count-only check fails RevisionChangeWithEqualCount_Refreshes.
// It does NOT exercise SlotAt's own call site (that class needs UnityEngine.Input and can't be built here);
// SlotAt just forwards to this helper, so a regression THERE (e.g. inlining a different check instead of
// calling ShouldRefresh) would not be caught by this suite.
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
