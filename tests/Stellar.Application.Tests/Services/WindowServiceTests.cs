using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public class WindowServiceTests
{
    // Minimal fake renderer recording calls; canvas always available, token is a boxed int.
    private sealed class FakeRenderer : IWindowRenderer
    {
        public int Mounts, Applies, Destroys; public bool Alive = true; public bool Canvas = true;
        public bool IsCanvasAvailable() => Canvas;
        public object? Mount(WindowRegistration reg) { Mounts++; return 1; }
        public bool IsAlive(object? token) => Alive && token != null;
        public bool LastHide;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { Applies++; LastHide = hide; }
        public void SetRect(object? token, WindowRect rect) { }
        public WindowRect GetRect(object? token) => new(0, 0, 0, 0);
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) => Destroys++;
    }

    private static WindowRegistration Reg(string id) =>
        new(new WindowSpec(id, id, new WindowRect(0, 0, 300, 200), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => true }, new TextElement(() => "hi"));

    private static (WindowService svc, FakeRenderer r) New()
    { var r = new FakeRenderer(); return (new WindowService(r, new NullLog()), r); }

    [Fact] public void HidesWhenShouldRenderIsFalse()
    {
        var r = new FakeRenderer();
        var draw = true;   // plugin-owned visibility predicate
        var svc = new WindowService(r, new NullLog());
        svc.Register(new WindowRegistration(
            new WindowSpec("h", "h", new WindowRect(0, 0, 300, 200), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { ShouldRender = () => draw }, new TextElement(() => "hi")));
        svc.Tick(0.2f); Assert.False(r.LastHide);   // predicate true → shown
        draw = false; svc.Tick(0.2f);
        Assert.True(r.LastHide);                     // predicate false → hidden
    }

    [Fact]
    public void Mounts_visible_window_on_first_tick()
    {
        var (svc, r) = New();
        svc.Register(Reg("w"));
        svc.Tick(0.2f);
        Assert.Equal(1, r.Mounts);
        Assert.True(r.Applies >= 1);   // first paint
    }

    [Fact] // A hidden EditModeDragOnly window is still enumerated as editable (Visible=false, CanHide=true).
    public void EditableElements_IncludesHidden()
    {
        var (svc, _) = New();
        svc.Register(new WindowRegistration(
            new WindowSpec("w.main", "W", new WindowRect(10, 20, 200, 100), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { ShouldRender = () => true, EditModeDragOnly = true }, new TextElement(() => "x")));
        svc.Tick(0.2f);                                          // mount

        svc.SetVisiblePersist("w.main", false); svc.Tick(0.2f);  // hide → destroy

        var els = new System.Collections.Generic.List<EditableElement>(svc.EditableElements());
        var e = Assert.Single(els);
        Assert.Equal("w.main", e.Id);
        Assert.False(e.Visible);
        Assert.True(e.CanHide);
    }

    [Fact]
    public void Caps_apply_to_interval_not_every_tick()
    {
        var (svc, r) = New();
        svc.Register(Reg("w"));
        svc.Tick(0.2f);              // mount + first apply
        var after = r.Applies;
        svc.Tick(0.01f);             // below ApplyInterval, not dirty
        Assert.Equal(after, r.Applies);
        svc.Tick(0.2f);              // crosses interval
        Assert.Equal(after + 1, r.Applies);
    }

    [Fact]
    public void Hidden_window_is_destroyed_and_not_reapplied()
    {
        var (svc, r) = New();
        var h = svc.Register(Reg("w"));
        svc.Tick(0.2f);
        h.SetVisible(false);
        svc.Tick(0.2f);
        Assert.Equal(1, r.Destroys);
    }

    [Fact]
    public void Self_heals_when_token_dies()
    {
        var (svc, r) = New();
        svc.Register(Reg("w"));
        svc.Tick(0.2f);
        r.Alive = false;            // simulate scene-change destroy
        svc.Tick(0.2f);
        Assert.Equal(2, r.Mounts);  // remounted
    }

    // Renderer that also exposes canvas metrics, with a toggleable "scale settled" signal, and records SetRect
    // calls — so we can assert the layout apply is DEFERRED while the CanvasScaler is unsettled (scene-change bug).
    private sealed class MetricsRenderer : IWindowRenderer, IWindowCanvasMetrics
    {
        public bool Ready; public int Mounts;
        public readonly System.Collections.Generic.List<WindowRect> SetRects = new();
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) { Mounts++; return 1; }
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) => SetRects.Add(rect);
        public WindowRect GetRect(object? token) => default;
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
        public float CanvasScale => 1f;
        public float UiScale => 1f;
        public bool CanvasScaleReady => Ready;
        public int CanvasGeneration => 1;
    }

    [Fact] // Scene-change bug guard: mount while the CanvasScaler is unsettled must PARK the layout apply (no clamp
           // against the transient default scale), then place it once scale is live.
    public void ApplySavedRect_defers_until_canvas_scale_ready()
    {
        var r = new MetricsRenderer { Ready = false };
        var svc = new WindowService(r, new NullLog());
        svc.Register(Reg("w"));
        svc.Tick(0.2f);                 // mount while scale NOT settled → apply parked
        Assert.Equal(1, r.Mounts);
        Assert.Empty(r.SetRects);       // deferred: nothing positioned against the shrunk (default-scale) bound
        r.Ready = true;
        svc.Tick(0.2f);                 // scaler settled → the parked apply runs
        Assert.NotEmpty(r.SetRects);    // window placed only once scale is live
    }

    [Fact]
    public void Duplicate_id_is_ignored()
    {
        var (svc, r) = New();
        svc.Register(Reg("w"));
        svc.Register(Reg("w"));
        svc.Tick(0.2f);
        Assert.Equal(1, r.Mounts);
    }

    // StartVisible=false reg (an on-demand dialog): stays hidden absent any persisted SHOW.
    private static WindowRegistration HiddenReg(string id) =>
        new(new WindowSpec(id, id, new WindowRect(0, 0, 300, 200), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => true, StartVisible = false }, new TextElement(() => "hi"));

    // --- Persisted-visibility seeding at Register (the layout-editor "hide sticks across relaunch" fix).
    // A window persisted HIDDEN must start Visible=false so it never mounts and never enters the fragile
    // one-shot ApplySavedRect restore race. Register overrides StartVisible to hidden ONLY on an actual
    // persisted hide — it never force-SHOWs a StartVisible=false window nor a persisted-visible one. ---

    [Fact]
    public void Persisted_hidden_StartVisible_window_starts_hidden_and_never_mounts()
    {
        var r = new FakeRenderer();
        var storage = new LayoutStorage(new InMemoryConfig(), new NullLog());
        var res = new Resolution(1920, 1080);
        storage.Save(storage.ActiveSlot, "w", res, new WindowRect(0, 0, 300, 200), visible: false);
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(storage, () => res);

        svc.Register(Reg("w"));   // StartVisible=true, but persisted hidden → seeded hidden
        svc.Tick(0.2f);

        Assert.Equal(0, r.Mounts);   // never mounted → no restore race, no flash
    }

    [Fact]
    public void Persisted_visible_window_is_not_force_hidden()
    {
        var r = new FakeRenderer();
        var storage = new LayoutStorage(new InMemoryConfig(), new NullLog());
        var res = new Resolution(1920, 1080);
        storage.Save(storage.ActiveSlot, "w", res, new WindowRect(10, 20, 300, 200), visible: true);
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(storage, () => res);

        svc.Register(Reg("w"));   // persisted VISIBLE → StartVisible=true honoured
        svc.Tick(0.2f);

        Assert.Equal(1, r.Mounts);
    }

    [Fact]
    public void StartVisible_false_untouched_window_stays_hidden()
    {
        var r = new FakeRenderer();
        var storage = new LayoutStorage(new InMemoryConfig(), new NullLog());
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(storage, () => new Resolution(1920, 1080));

        svc.Register(HiddenReg("w"));   // no persisted layout → seeding never force-SHOWs it
        svc.Tick(0.2f);

        Assert.Equal(0, r.Mounts);
    }

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }

    // Minimal in-memory IPluginConfig for constructing a real LayoutStorage in-test.
    private sealed class InMemoryConfig : IPluginConfig
    {
        private readonly System.Collections.Generic.Dictionary<string, InMemorySection> _sections = new();
#pragma warning disable CS0067
        public event System.Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sections.TryGetValue(name, out var s)) { s = new InMemorySection(); _sections[name] = s; }
            return s;
        }
    }

    private sealed class InMemorySection : IConfigSection
    {
        private readonly System.Collections.Generic.Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue) => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix)
        {
            foreach (var k in new System.Collections.Generic.List<string>(_store.Keys))
                if (k.StartsWith(prefix, System.StringComparison.Ordinal)) _store.Remove(k);
        }
    }
}
