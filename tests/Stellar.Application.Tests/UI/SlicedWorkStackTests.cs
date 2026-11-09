using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Ui;

/// <summary>The scheduling rules behind ConditionalElement.Prebuild (WindowBuilder.Prebuild.cs): a time-sliced build must
/// visit a tree in exactly the recursive builder's pre-order (sibling order = uGUI order, binding registration order),
/// stop when the frame budget is spent, resume where it stopped, and finish everything on demand.</summary>
public sealed class SlicedWorkStackTests
{
    // A tree as child lists: node → its children in order.
    private static readonly Dictionary<string, string[]> Tree = new()
    {
        ["root"] = new[] { "a", "b", "c" },
        ["a"] = new[] { "a1", "a2" },
        ["a2"] = new[] { "a2x" },
        ["c"] = new[] { "c1" },
    };

    private static readonly string[] PreOrder = { "root", "a", "a1", "a2", "a2x", "b", "c", "c1" };

    // Step = "build" the node, pushing its children (as BuildChild does while slicing).
    private static System.Action<string> Visitor(SlicedWorkStack<string> s, List<string> seen) => n =>
    {
        seen.Add(n);
        if (Tree.TryGetValue(n, out var kids)) foreach (var k in kids) s.Push(k);
    };

    [Fact]
    public void RunAllVisitsInRecursivePreOrder()
    {
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("root");
        Assert.Equal(PreOrder.Length, s.RunAll(Visitor(s, seen)));
        Assert.Equal(PreOrder, seen);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void BudgetStopsBetweenItemsAndResumesInOrder()
    {
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("root");
        long now = 0;
        var step = Visitor(s, seen);
        // Each item "costs" 1 tick; a 3-tick budget per frame → 3 items per Run.
        void Frame() { var deadline = now + 3; s.Run(n => { step(n); now++; }, () => now, deadline); }
        Frame();
        Assert.Equal(new[] { "root", "a", "a1" }, seen);
        Frame(); Frame();
        Assert.Equal(PreOrder, seen);
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void SpentBudgetDoesNothing()
    {
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("root");
        Assert.Equal(0, s.Run(Visitor(s, seen), () => 10, deadline: 10));
        Assert.Empty(seen);
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void AnItemIsNeverInterruptedSoOvershootIsOneItem()
    {
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("root");
        long now = 0;
        var step = Visitor(s, seen);
        // The first item alone blows the budget (5 ticks vs 3): it completes, nothing else runs this frame.
        Assert.Equal(1, s.Run(n => { step(n); now += 5; }, () => now, deadline: 3));
        Assert.Equal(new[] { "root" }, seen);
        Assert.Equal(3, s.Count);   // its three children are queued
    }

    [Fact]
    public void FinishingAfterSlicesCompletesTheRest()
    {
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("root");
        long now = 0;
        var step = Visitor(s, seen);
        s.Run(n => { step(n); now++; }, () => now, deadline: 2);   // a partial frame…
        s.RunAll(step);                                            // …then the branch is shown: finish it now
        Assert.Equal(PreOrder, seen);
    }

    [Fact]
    public void ClaimedExtraWorkRunsBeforeOlderSiblings()
    {
        // A nested Conditional's claimed branch is pushed by the item that registered it, so it runs before that item's
        // older siblings (DFS), after the item's own children.
        var s = new SlicedWorkStack<string>(); var seen = new List<string>();
        s.Push("p"); s.Push("q");
        s.RunAll(n => { seen.Add(n); if (n == "p") { s.Push("p1"); s.Push("claim"); } });
        Assert.Equal(new[] { "p", "p1", "claim", "q" }, seen);
    }

    [Fact]
    public void ClearDropsPendingAndPushedWork()
    {
        var s = new SlicedWorkStack<string>();
        s.Push("a"); s.TryPop(out _); s.Push("b"); s.Push("c");
        s.Clear();
        Assert.Equal(0, s.Count);
        Assert.False(s.TryPop(out _));
    }

    [Fact]
    public void AllowanceEndsOnUnitsOrTime()
    {
        var byUnits = new FrameAllowance(deadline: 100, units: 2);
        Assert.True(byUnits.TryTake(0));
        Assert.True(byUnits.TryTake(1));
        Assert.False(byUnits.TryTake(2));

        var byTime = new FrameAllowance(deadline: 5, units: 10);
        Assert.True(byTime.TryTake(4));
        Assert.False(byTime.TryTake(5));
    }
}
