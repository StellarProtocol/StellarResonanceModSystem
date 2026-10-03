using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Infrastructure.Hooks;
using Xunit;

namespace Stellar.Application.Tests.Game;

// Freeze review round 2: the freeze's leave prefix and the posing despawn prefix both sit on ZEntityMgr.RemoveEntity. The
// hooker kept ONE callback per method, so the second registration replaced the first and patched the trampoline twice.
// A second callback must chain (both run, in order; a throwing first never starves the second) and not re-patch. Do not weaken.
public sealed class HookCallbackTableTests
{
    private static readonly MethodBase Method = typeof(HookCallbackTableTests).GetMethod(nameof(Target), BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void hooker_second_callback_on_one_method_chains()
    {
        var table = new Dictionary<MethodBase, Action<object?, object?[]>>();
        var calls = new List<string>();
        Assert.True(HookCallbackTable.Add(table, Method, (_, _) => { calls.Add("posing"); throw new InvalidOperationException(); }));
        Assert.False(HookCallbackTable.Add(table, Method, (_, a) => calls.Add("freeze:" + a[0])));   // no second patch

        table[Method](null, new object?[] { 7L });
        Assert.Equal(new[] { "posing", "freeze:7" }, calls);
        Assert.Single(table);
    }

    // Review Minor finding (2026-10-02): PostfixResultAllOverloads used to overwrite the slot outright instead of
    // chaining — a second feature on the same result-postfixed method replaced the first's callback and would have
    // double-patched the trampoline. AddResult must behave exactly like Add.
    [Fact]
    public void hooker_result_second_callback_on_one_method_chains()
    {
        var table = new Dictionary<MethodBase, Action<object?>>();
        var calls = new List<string>();
        Assert.True(HookCallbackTable.AddResult(table, Method, r => { calls.Add("first:" + r); throw new InvalidOperationException(); }));
        Assert.False(HookCallbackTable.AddResult(table, Method, r => calls.Add("second:" + r)));   // no second patch

        table[Method](7L);
        Assert.Equal(new[] { "first:7", "second:7" }, calls);
        Assert.Single(table);
    }

    // Review Minor finding (2026-10-02): a failed FIRST patch used to leave its Add'd slot behind, so a later
    // registration attempt for the same method found an entry, chained onto it instead of re-patching, and the
    // callback was never wired to a real Harmony hook. Remove undoes the reservation so the next Add is treated as
    // the first one again.
    [Fact]
    public void hooker_failed_first_patch_is_removed_so_a_later_registration_retries()
    {
        var table = new Dictionary<MethodBase, Action<object?, object?[]>>();
        Assert.True(HookCallbackTable.Add(table, Method, (_, _) => { }));
        HookCallbackTable.Remove(table, Method);   // simulates the Harmony.Patch call that followed Add throwing
        Assert.True(HookCallbackTable.Add(table, Method, (_, _) => { }));   // treated as first again, not chained
        Assert.Single(table);
    }

    [Fact]
    public void hooker_result_failed_first_patch_is_removed_so_a_later_registration_retries()
    {
        var table = new Dictionary<MethodBase, Action<object?>>();
        Assert.True(HookCallbackTable.AddResult(table, Method, _ => { }));
        HookCallbackTable.RemoveResult(table, Method);
        Assert.True(HookCallbackTable.AddResult(table, Method, _ => { }));
        Assert.Single(table);
    }

    private static void Target() { }
}
