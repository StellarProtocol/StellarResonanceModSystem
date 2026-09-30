using System;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Infrastructure.Rendering;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Fix round 2: the main-thread resume pump lives exactly as long as the resume it serves (no fixed 60 s window,
// no idle per-frame work) and a resume that arrives with no pump live is still completed by the main-thread drain.
public sealed class ResumeQueueTests
{
    [Fact]
    public void Pump_runs_only_until_the_expected_resume_completes()
    {
        var q = new ResumeQueue();
        Assert.False(q.PumpShouldRun);           // idle: no per-frame work
        q.Expect();                               // a grab succeeded
        q.PumpStarted();
        Assert.True(q.PumpShouldRun);
        Assert.Equal(0, q.Drain());               // nothing requested yet: keep pumping
        Assert.True(q.PumpShouldRun);
        var tcs = new TaskCompletionSource<bool>();
        q.Enqueue(tcs);
        Assert.Equal(1, q.Drain());
        Assert.True(tcs.Task.IsCompletedSuccessfully);
        Assert.False(q.PumpShouldRun);            // served → the pump stops
        q.PumpStopped();
        Assert.Equal(0, q.Pumps);
    }

    [Fact]
    public void Completion_happens_on_the_draining_thread_not_the_requesting_one()
    {
        var q = new ResumeQueue();
        q.Expect();
        var tcs = new TaskCompletionSource<bool>();
        var completedOn = -1;
        tcs.Task.ContinueWith(_ => completedOn = Environment.CurrentManagedThreadId, TaskContinuationOptions.ExecuteSynchronously);
        var t = new Thread(() => q.Enqueue(tcs));
        t.Start();
        t.Join();
        Assert.False(tcs.Task.IsCompleted);       // the requester never completes it
        q.Drain();
        Assert.Equal(Environment.CurrentManagedThreadId, completedOn);
    }

    [Fact]
    public void A_resume_with_no_pump_live_is_flagged_for_the_tick_drain()
    {
        var q = new ResumeQueue();
        Assert.False(q.HasQueued);
        var tcs = new TaskCompletionSource<bool>();
        q.Enqueue(tcs);
        Assert.True(q.HasQueued);
        Assert.Equal(1, q.Drain());
        Assert.False(q.HasQueued);
        Assert.True(tcs.Task.IsCompleted);
    }

    [Fact]
    public void Two_captures_keep_the_pump_until_both_resumes_arrive()
    {
        var q = new ResumeQueue();
        q.Expect();
        q.Expect();
        q.Enqueue(new TaskCompletionSource<bool>());
        q.Drain();
        Assert.True(q.PumpShouldRun);
        q.Enqueue(new TaskCompletionSource<bool>());
        q.Drain();
        Assert.False(q.PumpShouldRun);
    }

    [Fact]
    public void Leak_guard_abandon_and_fail_all_leave_nothing_running()
    {
        var q = new ResumeQueue();
        q.Expect();
        q.PumpStarted();
        Assert.True(ResumeQueue.LeakGuardExpired(startedAt: 0f, now: ResumeQueue.LeakGuardSeconds + 1f));
        Assert.False(ResumeQueue.LeakGuardExpired(startedAt: 0f, now: 59f));
        q.Abandon();
        Assert.False(q.PumpShouldRun);
        var tcs = new TaskCompletionSource<bool>();
        q.Enqueue(tcs);
        q.FailAll(new InvalidOperationException("host gone"));
        Assert.True(tcs.Task.IsFaulted);
        Assert.Equal(0, q.Pumps);
        Assert.False(q.HasQueued);
    }
}
