using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Photo;

public sealed class RenderLookServiceTests
{
    private sealed class FakeBackend : ILookBackend
    {
        public LookCapabilities Capabilities { get; set; } = new((LookGroups)127);
        public readonly List<LookSettings?> Applied = new();
        public float? Focus;
        public readonly List<float> FocusUpdates = new();
        public void Apply(LookSettings? settings) => Applied.Add(settings);
        public void UpdateFocus(float distance) => FocusUpdates.Add(distance);
        public float? MeasureFocusDistance() => Focus;
    }

    private static readonly LookSettings Full = new()
    {
        Dof = new DofLook(), Color = new ColorLook { Saturation = -20 }, FilmGrain = new FilmGrainLook(),
    };

    [Fact]
    public void Dispose_turns_the_look_off()
    {
        var b = new FakeBackend();
        var h = new RenderLookService(b, _ => { }).Apply(Full);
        h.Dispose();
        Assert.Null(b.Applied[^1]);
        Assert.False(h.IsActive);
    }

    [Fact]
    public void Second_apply_replaces_first_and_warns()
    {
        var b = new FakeBackend();
        var warnings = new List<string>();
        var s = new RenderLookService(b, warnings.Add);
        var first = s.Apply(Full);
        var second = s.Apply(new LookSettings { Color = new ColorLook() });
        Assert.False(first.IsActive);
        Assert.True(second.IsActive);
        Assert.Single(warnings);
        var countBeforeStaleOps = b.Applied.Count;
        first.Update(Full);
        first.Dispose();
        Assert.Equal(countBeforeStaleOps, b.Applied.Count);
    }

    [Fact]
    public void PlayMode_strips_dof_and_film_grain()
    {
        var e = RenderLookService.Effective(Full with { PlayMode = true }, (LookGroups)127);
        Assert.Null(e.Dof);
        Assert.Null(e.FilmGrain);
        Assert.NotNull(e.Color);
    }

    [Fact]
    public void Unsupported_groups_are_stripped()
    {
        var e = RenderLookService.Effective(Full, LookGroups.Color);
        Assert.Null(e.Dof);
        Assert.Null(e.FilmGrain);
        Assert.NotNull(e.Color);
    }

    // Fix round 1 (#6): a moved focus writes ONLY the focus distance (UpdateFocus), never a full re-apply per frame.
    [Fact]
    public void Tick_updates_focus_only_when_tracking_and_moved()
    {
        var b = new FakeBackend { Focus = 4f };
        var s = new RenderLookService(b, _ => { });
        s.Apply(new LookSettings { Dof = new DofLook { FocusOnLocalPlayer = true } });
        var applies = b.Applied.Count;
        s.Tick();
        Assert.Equal(new[] { 4f }, b.FocusUpdates);
        s.Tick();                                   // not moved → nothing
        Assert.Equal(new[] { 4f }, b.FocusUpdates);
        Assert.Equal(applies, b.Applied.Count);     // no full re-apply from the focus path
    }

    [Fact]
    public void A_later_update_keeps_the_tracked_focus_distance()
    {
        var b = new FakeBackend { Focus = 4f };
        var s = new RenderLookService(b, _ => { });
        var h = s.Apply(new LookSettings { Dof = new DofLook { FocusOnLocalPlayer = true } });
        s.Tick();
        b.Focus = null;                              // measurement lost this frame
        h.Update(new LookSettings { Dof = new DofLook { FocusOnLocalPlayer = true }, Color = new ColorLook() });
        Assert.NotNull(b.Applied[^1]!.Color);
    }

    [Fact]
    public void Tick_does_nothing_without_tracking()
    {
        var b = new FakeBackend { Focus = 4f };
        var s = new RenderLookService(b, _ => { });
        s.Apply(new LookSettings { Dof = new DofLook() });
        var before = b.Applied.Count;
        s.Tick();
        Assert.Equal(before, b.Applied.Count);
        Assert.Empty(b.FocusUpdates);
    }
}
