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
        new(new WindowSpec(id, id, new WindowRect(0, 0, 300, 200), WindowCategory.Tools, WindowPanelStyle.GlassMenu),
            new TextElement(() => "hi"));

    private static (WindowService svc, FakeRenderer r) New()
    { var r = new FakeRenderer(); return (new WindowService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true }), r); }

    private static WindowRegistration RegHud(string id) =>
        new(new WindowSpec(id, id, new WindowRect(0, 0, 300, 200), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { AutoHideBehindGameMenus = true }, new TextElement(() => "hi"));

    [Fact] public void AutoHidesBehindFullScreenMenu()
    {
        var r = new FakeRenderer(); var menu = new StubMenuState();
        var svc = new WindowService(r, new NullLog(), menu, new StubClientState { IsLoggedIn = true });
        svc.Register(RegHud("h"));
        svc.Tick(0.2f); Assert.False(r.LastHide);              // menu closed → shown
        menu.IsFullScreenMenuOpen = true; svc.Tick(0.2f);
        Assert.True(r.LastHide);                                // menu open → hidden
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
            { EditModeDragOnly = true }, new TextElement(() => "x")));
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

    [Fact]
    public void Duplicate_id_is_ignored()
    {
        var (svc, r) = New();
        svc.Register(Reg("w"));
        svc.Register(Reg("w"));
        svc.Tick(0.2f);
        Assert.Equal(1, r.Mounts);
    }

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }
}
