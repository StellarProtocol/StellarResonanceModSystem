using System;
using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Photo Studio fw fix round (perf review): FindType walked every loaded assembly on every call, and a type missing
// after a game patch was re-walked forever. The registry now memoizes BOTH outcomes; a negative is forgotten when
// an assembly loads (the only way a missing type can appear) and when hot-update becomes ready.
public sealed class ReflectionGameTypeRegistryTests
{
    private sealed class CountingResolver
    {
        public readonly Dictionary<string, Type> Known = new();
        public int Calls;
        public Type? Resolve(string name) { Calls++; return Known.TryGetValue(name, out var t) ? t : null; }
    }

    [Fact]
    public void A_found_type_is_resolved_once()
    {
        var r = new CountingResolver();
        r.Known["A"] = typeof(string);
        var reg = new ReflectionGameTypeRegistry(r.Resolve);
        Assert.Same(typeof(string), reg.FindType("A"));
        Assert.Same(typeof(string), reg.FindType("A"));
        Assert.Equal(1, r.Calls);
    }

    [Fact]
    public void A_missing_type_is_memoized_as_a_negative()
    {
        var r = new CountingResolver();
        var reg = new ReflectionGameTypeRegistry(r.Resolve);
        Assert.Null(reg.FindType("Missing"));
        Assert.Null(reg.FindType("Missing"));
        Assert.Equal(1, r.Calls);
    }

    [Fact]
    public void Hot_update_ready_forgets_negatives_but_keeps_positives()
    {
        var r = new CountingResolver();
        r.Known["A"] = typeof(string);
        var reg = new ReflectionGameTypeRegistry(r.Resolve);
        reg.FindType("A");
        Assert.Null(reg.FindType("Late"));
        r.Known["Late"] = typeof(int);          // its assembly loaded
        reg.MarkHotUpdateReady();
        Assert.True(reg.IsHotUpdateReady);
        Assert.Same(typeof(int), reg.FindType("Late"));
        Assert.Same(typeof(string), reg.FindType("A"));
        Assert.Equal(3, r.Calls);               // A once, Late twice (negative, then re-resolved)
    }

    [Fact]
    public void An_assembly_load_forgets_negatives()
    {
        var r = new CountingResolver();
        var reg = new ReflectionGameTypeRegistry(r.Resolve);
        Assert.Null(reg.FindType("Late"));
        r.Known["Late"] = typeof(int);
        reg.ForgetNegatives();                   // what the AssemblyLoad handler calls
        Assert.Same(typeof(int), reg.FindType("Late"));
        Assert.False(reg.IsHotUpdateReady);
    }

    // Regression: a positive lookup used to be able to return a racing thread's null that hadn't been removed
    // yet, because FindType's positive branch used _memo.GetOrAdd(fullName, type) — which returns the EXISTING
    // entry when one is already there instead of overwriting it. Reproduced deterministically (no real threads,
    // so no flake) by making the resolver itself perform the racing negative write reentrantly, mid-resolution,
    // exactly as a second thread's TryAdd(fullName, null) could land between this thread's resolve() call and
    // its memo write.
    [Fact]
    public void A_racing_negative_written_during_resolution_never_shadows_the_positive()
    {
        ReflectionGameTypeRegistry? reg = null;
        var callCount = 0;
        Type? Resolve(string name)
        {
            callCount++;
            if (callCount != 1) return null;               // the racing (negative) thread's resolve
            Assert.Null(reg!.FindType(name));               // racing thread lands its null in the memo first
            return typeof(string);                          // this (positive) thread's own resolve succeeds
        }
        reg = new ReflectionGameTypeRegistry(Resolve);

        Assert.Same(typeof(string), reg.FindType("X"));     // must win over the racing null, not return it
        Assert.Same(typeof(string), reg.FindType("X"));     // and the positive must stick for later lookups
    }
}
