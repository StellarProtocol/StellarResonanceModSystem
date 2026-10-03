using System;
using System.Collections.Generic;
using System.Threading;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Game.Posing;
using Stellar.Infrastructure.Hooks;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Regression pin clone-nre-male-null-ridetpl, WIRING half — NEVER weaken or delete. Origin: probe run 6 (2026-10-02,
// /tmp/stellar-scenario-freecam-probe-1790912237.log; devkit .superpowers/sdd/posing/clone-nre-rootcause.md) + review of
// fw 63b44ca (I-1: the predicate/net tests did not cover PhotoCopyMaker.Make's wiring; M-1/M-4/M-5). Each test was
// checked to FAIL when its piece of wiring is removed (fix2 report, mutation table).
public sealed class CloneNreMaleNullRideTplMakeTests
{
    private const int EM = 1, EF = 2, Idle = 0, Action = 8;

    // ── (a) the guard runs before the copy, with "" and the ORIGINAL fade ─────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_idle_male_null_template_sets_empty_with_original_fade_before_clone()
    {
        var h = new Harness(EM, Idle, 0, templateNull: true);
        var copy = new object();
        h.Calls.OnClone = _ => copy;

        Assert.Same(copy, h.Maker.Make(h.Entity, h.Model));

        Assert.Equal(new[] { "set", "clone" }, h.Order);
        var set = Assert.Single(h.Ride.Sets);
        Assert.Same(h.Model, set.Model);
        Assert.Equal("", set.Template);
        Assert.Same(h.Ride.FadeValue, set.Fade);
    }

    // ── (b) every other source is left alone ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(EF, Idle, 0, true)]        // female: never takes the game's Replace path
    [InlineData(EM, Action, 9020, true)]   // acting male: the callback skips the template copy
    [InlineData(EM, Idle, 0, false)]       // "" or a real template
    public void clone_nre_male_null_ridetpl_make_female_acting_or_set_template_never_calls_the_setter(
        int gender, int state, int actionId, bool templateNull)
    {
        var h = new Harness(gender, state, actionId, templateNull);
        h.Calls.OnClone = _ => new object();

        h.Maker.Make(h.Entity, h.Model);

        Assert.Empty(h.Ride.Sets);
        Assert.Equal(new[] { "clone" }, h.Order);
    }

    // ── (c) a throwing clone's orphan reaches Recycle THROUGH Make, and the failure surfaces ──────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_throwing_clone_recycles_the_recorded_orphan_and_rethrows()
    {
        var h = new Harness(EM, Idle, 0, templateNull: true);
        h.Maker.Install(h.Hooks, h.Types);
        var orphan = new object();
        var boom = new NullReferenceException("SetAttrAnimRideTemplate addr.Replace");
        h.Calls.OnClone = _ =>
        {
            h.Hooks.Result!(orphan);   // the game's cloneModel postfix fires, then the callback throws
            throw boom;
        };

        var thrown = Assert.Throws<NullReferenceException>(() => h.Maker.Make(h.Entity, h.Model));

        Assert.Same(boom, thrown);
        Assert.Equal(new[] { orphan }, h.Calls.Recycled);
        Assert.Contains(h.Log.WarningLines, w => w.Contains("recycled=True"));
        Assert.False(h.Maker.CleanupArmed);
    }

    // ── (d) a successful Make disarms the net ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_success_disarms_the_net_and_recycles_nothing()
    {
        var h = new Harness(EF, Idle, 0, templateNull: true);
        h.Maker.Install(h.Hooks, h.Types);
        var copy = new object();
        var armedDuringClone = false;
        h.Calls.OnClone = _ =>
        {
            armedDuringClone = h.Maker.CleanupArmed;
            h.Hooks.Result!(copy);
            return copy;
        };

        Assert.Same(copy, h.Maker.Make(h.Entity, h.Model));

        Assert.True(armedDuringClone);
        Assert.False(h.Maker.CleanupArmed);
        Assert.Empty(h.Calls.Recycled);
        h.Hooks.Result!(new object());   // a later, unrelated cloneModel records nothing …
        h.Calls.OnClone = _ => throw new InvalidOperationException();
        Assert.Throws<InvalidOperationException>(() => h.Maker.Make(h.Entity, h.Model));
        Assert.Empty(h.Calls.Recycled);   // … so a later failure has nothing stale to remove
    }

    // ── EnsureHooks (PosingHookSet) installs the copy hook, and it is the one that feeds the net ──────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_ensure_hooks_installs_the_clone_postfix_once_after_arm()
    {
        var h = new Harness(EM, Idle, 0, templateNull: true);
        var set = new PosingHookSet(h.Types, h.Maker, (_, _) => { }, h.Log);

        set.Ensure();   // not armed yet: nothing
        Assert.Empty(h.Hooks.Installs);

        set.Arm(h.Hooks);
        set.Ensure();
        set.Ensure();

        Assert.Equal(new[] { "result ZModelManager.cloneModel", "prefix ZEntityMgr.RemoveEntity" },
            h.Hooks.Installs);
        var orphan = new object();
        h.Calls.OnClone = _ =>
        {
            h.Hooks.Result!(orphan);
            throw new NullReferenceException();
        };
        Assert.Throws<NullReferenceException>(() => h.Maker.Make(h.Entity, h.Model));
        Assert.Equal(new[] { orphan }, h.Calls.Recycled);
    }

    // ── M-1: a missing setter turns the guard off with ONE warning; the lookup is not retried ────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_missing_setter_warns_once_and_still_copies()
    {
        var h = new Harness(EM, Idle, 0, templateNull: true);
        h.Ride.MissingMember = "SetAttrAnimRideTemplate";
        h.Calls.OnClone = _ => new object();

        h.Maker.Make(h.Entity, h.Model);
        h.Maker.Make(h.Entity, h.Model);

        Assert.Equal(new[] { "[Posing] ride-template guard unavailable (SetAttrAnimRideTemplate not found)" },
            h.Log.WarningLines);
        Assert.Empty(h.Ride.Sets);
        Assert.Equal(new[] { "clone", "clone" }, h.Order);
    }

    [Fact]
    public void clone_nre_male_null_ridetpl_make_ride_template_member_miss_is_cached()
    {
        var types = new FakeTypes(typeof(object), GameEntityAccess.ModelType, GameEntityAccess.EntityType,
            GameEntityAccess.AttrExtType);
        var calls = new RideTemplateCalls(types);

        Assert.Equal("ZModel.ModelGender", calls.Missing());
        var lookups = types.Lookups;
        Assert.Equal("ZModel.ModelGender", calls.Missing());
        Assert.Equal(lookups, types.Lookups);   // the second ask resolved nothing again
    }

    // ── M-4: the orphan message carries RecyclePhotoModel's result ──────────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_unavailable_recycle_is_reported_as_not_removed()
    {
        var h = new Harness(EM, Idle, 0, templateNull: true);
        h.Maker.Install(h.Hooks, h.Types);
        h.Calls.RecycleResult = false;
        h.Calls.OnClone = _ =>
        {
            h.Hooks.Result!(new object());
            throw new NullReferenceException();
        };

        Assert.Throws<NullReferenceException>(() => h.Maker.Make(h.Entity, h.Model));

        var w = Assert.Single(h.Log.WarningLines);
        Assert.Contains("recycled=False", w);
        Assert.Contains("could NOT be removed", w);
    }

    // ── M-5: a cloneModel from another thread while armed is not ours ───────────────────────────────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_record_from_another_thread_while_armed_is_ignored()
    {
        var net = new CloneOrphanNet();
        var recycled = new List<object>();
        Assert.Throws<InvalidOperationException>(() => net.Run(() =>
        {
            var other = new Thread(() => net.Record(new object()));
            other.Start();
            other.Join();
            throw new InvalidOperationException();
        }, o => { recycled.Add(o); return true; }, _ => { }));

        Assert.Empty(recycled);
        Assert.Equal(0, net.LastRecords);
    }

    // ── I-2 diagnostics count: every model the armed call made is counted, the first is the copy ────────────────────

    [Fact]
    public void clone_nre_male_null_ridetpl_make_net_counts_every_record_and_keeps_the_first()
    {
        var net = new CloneOrphanNet();
        var first = new object();
        var recycled = new List<object>();
        Assert.Throws<InvalidOperationException>(() => net.Run(() =>
        {
            net.Record(first);
            net.Record(new object());   // a mounted copy is 2 models (sweep 2026-10-02)
            throw new InvalidOperationException();
        }, o => { recycled.Add(o); return true; }, _ => { }));

        Assert.Equal(new[] { first }, recycled);
        Assert.Equal(2, net.LastRecords);
    }

    // ── fakes ────────────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed class Harness
    {
        public readonly List<string> Order = new();
        public readonly StubLog Log = new();
        public readonly FakeCopyCalls Calls;
        public readonly FakeRide Ride;
        public readonly FakeHooks Hooks = new();
        public readonly FakeTypes Types = new(null, PhotoCopyMaker.ModelManagerType, GameEntityAccess.ManagerType);
        public readonly PhotoCopyMaker Maker;
        public readonly object Entity = new(), Model = new();

        public Harness(int gender, int state, int actionId, bool templateNull)
        {
            Calls = new FakeCopyCalls(Order);
            Ride = new FakeRide(Order) { GenderValue = gender, StateValue = state, ActionIdValue = actionId, Null = templateNull };
            Maker = new PhotoCopyMaker(Calls, Ride, Log);
        }
    }

    private sealed class FakeCopyCalls : IPhotoCopyCalls
    {
        private readonly List<string> _order;
        public Func<object, object?> OnClone = _ => null;
        public readonly List<object> Recycled = new();
        public bool RecycleResult = true;
        public FakeCopyCalls(List<string> order) => _order = order;
        public object? Clone(object entity) { _order.Add("clone"); return OnClone(entity); }
        public bool Recycle(object copy) { Recycled.Add(copy); return RecycleResult; }
        public int ModelCount() => 100;
        public void DiagnosticsTo(Action<string> log) { }
    }

    private sealed class FakeRide : IRideTemplateAccess
    {
        private readonly List<string> _order;
        public int GenderValue, StateValue, ActionIdValue;
        public bool Null;
        public string? MissingMember;
        public readonly object FadeValue = "fade-0.25";
        private object _currentFade;
        public readonly List<(object Model, string Template, object? Fade)> Sets = new();
        public FakeRide(List<string> order)
        {
            _order = order;
            _currentFade = FadeValue;
        }
        public string? Missing() => MissingMember;
        public int Gender(object model) => GenderValue;
        public int State(object entity) => StateValue;
        public int ActionId(object model) => ActionIdValue;
        public bool TemplateIsNull(object model) => Null;
        public object? Fade(object model) => _currentFade;
        // A write moves the current fade, so a fade read AFTER the write would hand SetTemplate a different object and
        // fail the "ORIGINAL fade" assertion.
        public void SetTemplate(object model, string template, object? fade)
        {
            _order.Add("set");
            Sets.Add((model, template, fade));
            _currentFade = "fade-after-write";
        }
    }

    private sealed class FakeHooks : IGameMethodHooks
    {
        public readonly List<string> Installs = new();
        public Action<object?>? Result;
        public void PrefixAllOverloads(Type type, string methodName, Action<object?, object?[]> callback) =>
            Installs.Add($"prefix {Name(type)}.{methodName}");
        public void PostfixResultAllOverloads(Type type, string methodName, Action<object?> callback)
        {
            Installs.Add($"result {Name(type)}.{methodName}");
            Result = callback;
        }
        private static string Name(Type t) => t.Name;
    }

    // Game type names -> stand-in types (one shared type, or a marker named after the game type).
    private sealed class FakeTypes : IGameTypeRegistry
    {
        private readonly Dictionary<string, Type> _map = new();
        public int Lookups;
        public FakeTypes(Type? shared, params string[] names)
        {
            foreach (var n in names) _map[n] = shared ?? Marker(n);
        }
        public Type? FindType(string fullName) { Lookups++; return _map.TryGetValue(fullName, out var t) ? t : null; }
        private static Type Marker(string name) => name switch
        {
            PhotoCopyMaker.ModelManagerType => typeof(ZModelManager),
            GameEntityAccess.ManagerType => typeof(ZEntityMgr),
            _ => throw new ArgumentException(name),
        };
    }

    private sealed class ZModelManager { }
    private sealed class ZEntityMgr { }
}
