using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// WindowSpec.Prebuild (WindowService.Prebuild.cs): structure at the login screen, one off-screen fill in the world, the
// window hidden throughout (IsShown false, never shown by a restore), a real open mid-fill takes over, stale content
// refills, a world going down aborts the fill. Plus the pure stage / pacing rules.
public sealed class WindowPrebuildTests
{
    private static readonly Resolution Res = new(2560, 1440);
    private static readonly PrebuildGate Title = new(PrebuildStage.Structure, InWorld: false, BehindLoading: false, InCombat: false, Busy: false);
    private static readonly PrebuildGate Loading = new(PrebuildStage.Fill, InWorld: true, BehindLoading: true, InCombat: false, Busy: false);
    private static readonly PrebuildGate Play = new(PrebuildStage.Fill, InWorld: true, BehindLoading: false, InCombat: false, Busy: false);

    private sealed class Renderer : IWindowRenderer, IWindowOrder, IWindowParking, IWindowStructureMount, IWindowBackgroundWarm
    {
        public readonly List<string> Calls = new();
        public readonly HashSet<string> Warm = new();
        public bool Done = true;
        public int Applies;
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) { Calls.Add("mount"); return 1; }
        public object? MountStructure(WindowRegistration reg) { Calls.Add("structure"); return 1; }
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { Applies++; Calls.Add(Warm.Contains(reg.Spec.Id) ? "apply:bg" : "apply"); }
        public void SetRect(object? token, WindowRect rect) { }
        public WindowRect GetRect(object? token) => new(10, 10, 100, 100);
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) => Calls.Add("destroy");
        public void BringToFront(object? token) => Calls.Add("front");
        public void Activate(object? token) => Calls.Add("activate");
        public void SetTopmost(object? token, bool topmost) { }
        public void Park(object? token) => Calls.Add("park");
        public void Unpark(object? token) => Calls.Add("unpark");
        public void SetBackgroundWarm(string windowId, bool on) { if (on) Warm.Add(windowId); else Warm.Remove(windowId); Calls.Add("warm:" + on); }
        public bool IsBackgroundWarmDone(string windowId) => Done;
    }

    private static (WindowService svc, Renderer r, IWindowControl h) Rig(bool prebuild = true, System.Func<int>? sig = null,
        System.Func<bool>? render = null, bool startVisible = false)
    {
        var r = new Renderer();
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(new LayoutStorage(new Config(), new NullLog()), () => Res);
        var spec = new WindowSpec("w", "", new WindowRect(10, 10, 100, 100), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { StartVisible = startVisible, ShouldRender = render ?? (() => true), Prebuild = prebuild, PrebuildSignature = sig };
        return (svc, r, svc.Register(new WindowRegistration(spec, new TextElement(() => "x"))));
    }

    // One Host tick: the prebuild pump BEFORE the window tick (Wiring.ServiceTick.cs order).
    private static void Step(WindowService svc, PrebuildGate gate, int ticks = 1)
    {
        for (var i = 0; i < ticks; i++) { svc.TickPrebuilds(gate, 0.2f); svc.Tick(0.2f); }
    }

    [Fact]
    public void Title_screen_builds_structure_only_and_never_shows_it()
    {
        var (svc, r, h) = Rig();
        Step(svc, Title, 3);
        Assert.Equal(new[] { "structure" }, r.Calls.ToArray());   // no apply = no plugin value lambda before the world
        Assert.False(h.IsShown);
    }

    [Fact]
    public void Opt_out_window_is_never_touched()
    {
        var (svc, r, _) = Rig(prebuild: false);
        Step(svc, Title, 3); Step(svc, Loading, 10);
        Assert.Empty(r.Calls);
    }

    [Fact]
    public void World_fills_the_structure_off_screen_then_parks_it_hidden()
    {
        var (svc, r, h) = Rig();
        Step(svc, Title);
        r.Calls.Clear();
        Step(svc, Loading, 6);
        Assert.Equal(new[] { "warm:True", "unpark", "apply:bg", "apply:bg", "apply:bg", "warm:False", "park" }, r.Calls.ToArray());
        Assert.False(h.IsShown);
        r.Calls.Clear();
        Step(svc, Loading, 10);
        Assert.Empty(r.Calls);   // filled once
    }

    [Fact]
    public void A_window_never_structure_built_is_mounted_by_the_fill()
    {
        var (svc, r, h) = Rig();
        Step(svc, Play, 5);
        Assert.Equal("warm:True", r.Calls[0]);
        Assert.Equal("mount", r.Calls[1]);
        Assert.Contains("park", r.Calls);
        Assert.False(h.IsShown);
    }

    [Fact]
    public void First_real_open_after_a_fill_is_an_unpark_on_top_with_no_mount()
    {
        var (svc, r, h) = Rig();
        Step(svc, Title); Step(svc, Loading, 6);
        r.Calls.Clear();
        h.SetVisible(true); Step(svc, Play);
        Assert.Equal(new[] { "unpark", "activate", "apply" }, r.Calls.ToArray());
        Assert.True(h.IsShown);
    }

    [Fact]
    public void A_real_open_mid_fill_takes_over()
    {
        var (svc, r, h) = Rig();
        r.Done = false;
        Step(svc, Title); Step(svc, Loading, 2);   // filling
        r.Calls.Clear();
        h.SetVisible(true); Step(svc, Loading);
        Assert.Equal(new[] { "warm:False", "activate", "apply" }, r.Calls.ToArray());
        Assert.True(h.IsShown);
        r.Calls.Clear();
        Step(svc, Loading, 5);
        Assert.DoesNotContain("warm:True", r.Calls);   // shown → counts as filled
    }

    [Fact]
    public void World_going_down_aborts_the_fill_and_refills_later()
    {
        var (svc, r, _) = Rig();
        r.Done = false;
        Step(svc, Title); Step(svc, Play, 2);
        r.Calls.Clear();
        Step(svc, Title);   // zone load / logout: world down
        Assert.Equal(new[] { "warm:False", "park" }, r.Calls.ToArray());   // parked in the same tick, no further apply
        r.Done = true; r.Calls.Clear();
        Step(svc, Play, 20);   // in play: waits PlayGapSeconds after the aborted fill
        Assert.Contains("warm:True", r.Calls);
    }

    [Fact]
    public void Play_fills_wait_for_the_gap_and_out_of_combat()
    {
        var (svc, r, _) = Rig();
        var combat = Play with { InCombat = true };
        Step(svc, Title);
        r.Calls.Clear();
        Step(svc, combat, 20);
        Assert.Empty(r.Calls);
        Step(svc, Play);
        Assert.Contains("warm:True", r.Calls);
    }

    [Fact]
    public void Changed_signature_while_hidden_refills()
    {
        var content = 1;
        var (svc, r, _) = Rig(sig: () => content);
        Step(svc, Title); Step(svc, Loading, 6);
        r.Calls.Clear();
        Step(svc, Loading, 5);
        Assert.Empty(r.Calls);
        content = 2;
        Step(svc, Loading, 6);
        Assert.Contains("warm:True", r.Calls);
    }

    [Fact]
    public void New_login_into_the_world_refills()
    {
        var (svc, r, _) = Rig();
        Step(svc, Title); Step(svc, Loading, 6);
        Step(svc, Title);   // back to character select
        r.Calls.Clear();
        Step(svc, Loading, 6);
        Assert.Contains("warm:True", r.Calls);
    }

    [Fact]
    public void Visible_window_held_hidden_by_ShouldRender_is_filled_until_it_draws()
    {
        var draw = false;
        var (svc, r, h) = Rig(render: () => draw, startVisible: true);
        Step(svc, Title);   // Visible: the normal path mounts it (suppressed); no structure stage for it
        Assert.Equal(new[] { "mount", "apply" }, r.Calls.ToArray());
        r.Calls.Clear(); r.Done = false;
        Step(svc, Loading, 2);
        Assert.Equal(new[] { "warm:True", "apply:bg", "apply:bg" }, r.Calls.ToArray());
        r.Calls.Clear();
        draw = true; Step(svc, Loading);   // ShouldRender turns true: the draw wins, no re-activation (not a SetVisible)
        Assert.Equal(new[] { "warm:False", "apply" }, r.Calls.ToArray());
        r.Calls.Clear();
        draw = false; Step(svc, Loading, 10);   // drawn once → filled; hidden again by ShouldRender → no refill
        Assert.DoesNotContain("warm:True", r.Calls);
    }

    [Fact]
    public void Slot_switch_never_shows_a_window_only_the_prebuild_mounted()
    {
        var (svc, r, h) = Rig();
        Step(svc, Title);
        svc.ReapplyLayout(applyVisibility: true);   // nothing persisted → a restore would read "shown"
        Step(svc, Title);
        Assert.False(h.IsShown);
    }

    [Fact]
    public void Busy_framework_warm_starts_nothing()
    {
        var (svc, r, _) = Rig();
        Step(svc, Title with { Busy = true }, 3);
        Assert.Empty(r.Calls);
    }

    [Theory]
    [InlineData(GamePhase.Startup, false, true, true, (int)PrebuildStage.None)]
    [InlineData(GamePhase.TitleScreen, false, false, true, (int)PrebuildStage.None)]        // canvas not settled
    [InlineData(GamePhase.TitleScreen, false, true, false, (int)PrebuildStage.None)]        // language unknown: labels would bake English
    [InlineData(GamePhase.TitleScreen, false, true, true, (int)PrebuildStage.Structure)]
    [InlineData(GamePhase.CharSelect, false, true, false, (int)PrebuildStage.Structure)]
    [InlineData(GamePhase.World, false, true, true, (int)PrebuildStage.Structure)]          // zone load: world down → no values
    [InlineData(GamePhase.World, true, true, true, (int)PrebuildStage.Fill)]
    public void Stage_rules(GamePhase phase, bool worldActive, bool canvasReady, bool languageKnown, int expected) =>
        Assert.Equal((PrebuildStage)expected, WindowPrebuildRules.StageFor(phase, worldActive, canvasReady, languageKnown));

    [Theory]
    [InlineData(true, true, 0f, true)]       // behind a loading screen: always
    [InlineData(false, true, 99f, false)]    // in play: never in combat
    [InlineData(false, false, 1f, false)]    // in play: wait the gap
    [InlineData(false, false, 3f, true)]
    public void Fill_pacing(bool behindLoading, bool inCombat, float since, bool expected) =>
        Assert.Equal(expected, WindowPrebuildRules.MayStartFill(behindLoading, inCombat, since));

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }

    private sealed class Config : IPluginConfig
    {
        private readonly Dictionary<string, Section> _sections = new();
#pragma warning disable CS0067
        public event System.Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sections.TryGetValue(name, out var s)) { s = new Section(); _sections[name] = s; }
            return s;
        }
    }

    private sealed class Section : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue) => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix)
        {
            foreach (var k in new List<string>(_store.Keys))
                if (k.StartsWith(prefix, System.StringComparison.Ordinal)) _store.Remove(k);
        }
    }
}
