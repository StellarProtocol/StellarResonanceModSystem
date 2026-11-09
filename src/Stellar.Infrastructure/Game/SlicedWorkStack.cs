using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The scheduling half of a time-sliced tree build (Conditional <c>Prebuild</c>, WindowBuilder.Prebuild.cs): a
/// depth-first work stack that a recursive builder can stop and resume between any two items. Pure (no Unity) so the
/// order and budget rules are unit-tested.
/// <para>ORDER: while one item is processed, everything it <see cref="Push"/>es (its children, in order) is held
/// aside and moved onto the stack — reversed — only when the next item is taken. So the children of the item just
/// processed come next, first child first, before any older sibling: exactly the pre-order of the plain recursive
/// build. That keeps sibling order (uGUI draw/layout order) and the order bindings are registered identical to the
/// lazy path.</para>
/// <para>BUDGET: <see cref="Run"/> processes items until the stack is empty or the clock reaches the deadline. An item
/// is never interrupted (one item = one element build), so a frame overshoots by at most one item; an empty budget
/// (deadline already passed — another window spent this frame's share) does nothing.</para>
/// </summary>
internal sealed class SlicedWorkStack<T>
{
    private readonly List<T> _stack = new();     // top = last
    private readonly List<T> _pushed = new();    // pushed by the item being processed, in push order

    /// <summary>Items still to process (including ones pushed by the current item).</summary>
    public int Count => _stack.Count + _pushed.Count;

    /// <summary>Queue an item to run before every item queued earlier (a child of the item being processed).</summary>
    public void Push(T item) => _pushed.Add(item);

    /// <summary>Take the next item in pre-order.</summary>
    public bool TryPop(out T item)
    {
        for (var i = _pushed.Count - 1; i >= 0; i--) _stack.Add(_pushed[i]);
        _pushed.Clear();
        if (_stack.Count == 0) { item = default!; return false; }
        item = _stack[_stack.Count - 1];
        _stack.RemoveAt(_stack.Count - 1);
        return true;
    }

    /// <summary>Process items until the stack is empty or <paramref name="clock"/> reaches <paramref name="deadline"/>
    /// (checked before each item). Returns how many ran.</summary>
    public int Run(Action<T> step, Func<long> clock, long deadline)
    {
        var n = 0;
        while (clock() < deadline && TryPop(out var item)) { step(item); n++; }
        return n;
    }

    /// <summary>Process everything now, ignoring the budget (a branch about to be shown must never be half-built).</summary>
    public int RunAll(Action<T> step)
    {
        var n = 0;
        while (TryPop(out var item)) { step(item); n++; }
        return n;
    }

    /// <summary>Drop all pending work (an aborted branch).</summary>
    public void Clear() { _stack.Clear(); _pushed.Clear(); }
}

/// <summary>A per-frame allowance of time AND units (the reveal step of a prebuild: each unit's real cost lands later,
/// in Unity's canvas rebuild, so a unit cap bounds what the stopwatch can't see). Pure; unit-tested.</summary>
internal struct FrameAllowance
{
    private readonly long _deadline;
    private int _units;

    public FrameAllowance(long deadline, int units) { _deadline = deadline; _units = units; }

    /// <summary>True (and one unit spent) while time and units remain.</summary>
    public bool TryTake(long now)
    {
        if (now >= _deadline || _units <= 0) return false;
        _units--;
        return true;
    }
}
