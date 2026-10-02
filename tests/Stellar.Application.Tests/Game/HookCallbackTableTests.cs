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

    private static void Target() { }
}
