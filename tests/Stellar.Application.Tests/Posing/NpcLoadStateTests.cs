using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Task 4 review carry-overs on the NPC load (spec § 4.4): the callbacks' keep-alive is held only while the load is
// pending and released on load, on error and on a close while pending (a camera release — zone change included — closes
// every model); a model reference is dropped the moment it is handed out for removal; a model that arrives after a close
// is handed out for removal at once and never becomes posable.
public sealed class NpcLoadStateTests
{
    private static readonly object[] KeepAlive = { new object() };

    private static NpcLoadState Requested()
    {
        var s = new NpcLoadState();
        s.Requested(KeepAlive);
        Assert.True(s.Holding);
        return s;
    }

    [Fact]
    public void A_load_releases_the_keep_alive_and_makes_the_model_posable()
    {
        var s = Requested();
        var model = new object();
        s.Created(model);
        Assert.Equal(NpcLoadStep.Ready, s.Loaded(model, out var target));
        Assert.Same(model, target);
        Assert.Same(model, s.Model);
        Assert.False(s.Holding);
    }

    // Review round 1, finding 1: on an error after preCreate already handed over a model, the generate path does not own
    // it (the game's caller keeps a half-made model for its own cleanup only up to that point) — Failed() must hand it
    // out for removal exactly once, the same way Close() does, instead of silently dropping it (a dropped model could
    // stay visible as a duplicate NPC).
    [Fact]
    public void An_error_releases_the_keep_alive_and_hands_out_a_half_made_model_for_removal_exactly_once()
    {
        var s = Requested();
        var model = new object();
        s.Created(model);
        Assert.Equal(NpcLoadStep.Fail, s.Failed(out var target));
        Assert.Same(model, target);
        Assert.False(s.Holding);
        Assert.Null(s.Model);
        Assert.Equal(NpcLoadStep.Ignore, s.Failed(out var again));
        Assert.Null(again);
    }

    [Fact]
    public void A_close_while_pending_releases_the_keep_alive()
    {
        var s = Requested();
        Assert.Null(s.Close());
        Assert.False(s.Holding);
    }

    [Fact]
    public void A_model_arriving_after_a_close_is_handed_out_for_removal_and_forgotten()
    {
        var s = Requested();
        var model = new object();
        s.Created(model);
        Assert.Null(s.Close());
        Assert.Equal(NpcLoadStep.Recycle, s.Loaded(model, out var target));
        Assert.Same(model, target);
        Assert.Null(s.Model);
        Assert.False(s.Holding);
    }

    [Fact]
    public void A_late_load_without_a_pre_create_still_hands_out_the_arriving_model()
    {
        var s = Requested();
        s.Close();
        var model = new object();
        Assert.Equal(NpcLoadStep.Recycle, s.Loaded(model, out var target));
        Assert.Same(model, target);
        Assert.Null(s.Model);
    }

    [Fact]
    public void Closing_a_loaded_model_hands_it_out_once_and_forgets_it()
    {
        var s = Requested();
        var model = new object();
        s.Loaded(model, out _);
        Assert.Same(model, s.Close());
        Assert.Null(s.Model);
        Assert.Null(s.Close());
    }

    [Fact]
    public void A_second_callback_hands_out_nothing_and_keeps_the_posable_model()
    {
        var s = Requested();
        var model = new object();
        s.Loaded(model, out _);
        Assert.Equal(NpcLoadStep.Ignore, s.Loaded(new object(), out var again));
        Assert.Null(again);
        Assert.Equal(NpcLoadStep.Ignore, s.Failed(out var abandoned));
        Assert.Null(abandoned);
        Assert.Same(model, s.Model);
    }

    // Same root cause as the finding-1 regression above: a pre-created model abandoned by a close-then-error is still
    // the generate path NOT owning it — Failed() hands it out here too, even though the step itself reports Ignore
    // (already closed).
    [Fact]
    public void An_error_after_a_close_still_hands_out_the_pre_created_model()
    {
        var s = Requested();
        var model = new object();
        s.Created(model);
        s.Close();
        Assert.Equal(NpcLoadStep.Ignore, s.Failed(out var target));
        Assert.Same(model, target);
        Assert.Null(s.Model);
        Assert.False(s.Holding);
    }

    // Review round 1, finding 2: a "successful" load callback with nothing to pose (no pre-create was seen and the
    // callback's own model argument is null) must not leave the person stuck Loading forever — treat it as a failure.
    [Fact]
    public void A_load_with_nothing_to_pose_fails_instead_of_sticking_in_loading()
    {
        var s = Requested();
        Assert.Equal(NpcLoadStep.Fail, s.Loaded(null, out var target));
        Assert.Null(target);
        Assert.Null(s.Model);
        Assert.False(s.Holding);
    }

    [Fact]
    public void A_load_inside_the_request_never_re_holds_the_callbacks()
    {
        var s = new NpcLoadState();
        s.Loaded(new object(), out _);   // the game called back before the request returned
        s.Requested(KeepAlive);
        Assert.False(s.Holding);
    }

    [Fact]
    public void A_close_before_the_request_returns_never_holds_the_callbacks()
    {
        var s = new NpcLoadState();
        s.Close();
        s.Requested(KeepAlive);
        Assert.False(s.Holding);
    }
}
