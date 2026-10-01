using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// The freeze backend's "a failure in one never stops the others" contract (GameFreezeBackend.FreezeAll /
// UnfreezeAll) needs IL2CPP to exercise end to end — this pins the pure seam it runs through (review finding,
// Task 9 round 1: a throw in one phase must never skip the rest, so the backend never ends up stuck frozen).
public sealed class FreezeStepRunnerTests
{
    [Fact]
    public void All_steps_run_even_when_one_throws()
    {
        var ran = new List<int>();
        var ex = FreezeStepRunner.RunAll(
            () => ran.Add(0),
            () => throw new InvalidOperationException("boom"),
            () => ran.Add(2));
        Assert.Equal(new[] { 0, 2 }, ran);
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal("boom", ex!.Message);
    }

    [Fact]
    public void The_first_exception_is_reported_when_several_steps_throw()
    {
        var order = new List<string>();
        var ex = FreezeStepRunner.RunAll(
            () => { order.Add("a"); throw new InvalidOperationException("first"); },
            () => { order.Add("b"); throw new ArgumentException("second"); });
        Assert.Equal(new[] { "a", "b" }, order);   // both ran
        Assert.Equal("first", ex!.Message);        // only the first is reported
    }

    [Fact]
    public void Null_when_every_step_succeeds() => Assert.Null(FreezeStepRunner.RunAll(() => { }, () => { }));

    [Fact]
    public void No_steps_is_a_no_op() => Assert.Null(FreezeStepRunner.RunAll());
}
