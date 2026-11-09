using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Soft park (2026-11-09): the Settings hub hides without deactivating its root, and the window service keeps
/// it current in the background while hidden (IWindowParkedRefresh). These pin who gets a background apply, and when.</summary>
public sealed class WindowParkedRefreshTests
{
    private sealed class Renderer : IWindowRenderer, IWindowParking, IWindowParkedRefresh
    {
        public int Applies, Parks, Refreshes, Asks;
        public bool Wants = true;
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) => 1;
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) => Applies++;
        public void SetRect(object? token, WindowRect rect) { }
        public WindowRect GetRect(object? token) => default;
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
        public void Park(object? token) => Parks++;
        public void Unpark(object? token) { }
        public bool WantsParkedRefresh(object? token) { Asks++; return Wants; }
        public void RefreshParked(object? token) => Refreshes++;
    }

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }

    // Per test instance: xunit may run this class's tests in parallel, so no static gate.
    private bool _gate = true;

    private WindowRegistration Reg(string id) =>
        new(new WindowSpec(id, id, new WindowRect(0, 0, 300, 200), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => _gate }, new TextElement(() => "hi"));

    private (WindowService svc, Renderer r, IWindowControl h) Mounted()
    {
        _gate = true;
        var r = new Renderer();
        var svc = new WindowService(r, new NullLog());
        var h = svc.Register(Reg("w"));
        svc.Tick(0.2f);   // mount + first apply
        return (svc, r, h);
    }

    [Fact]
    public void A_shown_window_is_never_background_applied()
    {
        var (svc, r, _) = Mounted();
        for (var i = 0; i < 5; i++) svc.Tick(0.2f);
        Assert.Equal(0, r.Asks);
        Assert.Equal(0, r.Refreshes);
    }

    [Fact]
    public void A_hidden_parked_window_is_refreshed_on_capped_ticks_when_the_renderer_wants_it()
    {
        var (svc, r, h) = Mounted();
        h.SetVisible(false);
        svc.Tick(0.2f);                 // parks, then one capped tick
        svc.Tick(0.01f);                // below the apply interval: not asked
        svc.Tick(0.2f);
        Assert.Equal(1, r.Parks);
        Assert.Equal(2, r.Asks);
        Assert.Equal(2, r.Refreshes);
    }

    [Fact]
    public void The_renderer_decides_the_cadence()
    {
        var (svc, r, h) = Mounted();
        r.Wants = false;
        h.SetVisible(false);
        for (var i = 0; i < 4; i++) svc.Tick(0.2f);
        Assert.Equal(4, r.Asks);
        Assert.Equal(0, r.Refreshes);
    }

    [Fact]
    public void A_gated_hidden_window_gets_no_background_apply()
    {
        var (svc, r, h) = Mounted();
        h.SetVisible(false);
        _gate = false;                  // e.g. behind the loading screen
        svc.Tick(0.2f); svc.Tick(0.2f);
        Assert.Equal(0, r.Refreshes);
        _gate = true;
        svc.Tick(0.2f);
        Assert.Equal(1, r.Refreshes);
    }

    [Fact]
    public void Showing_again_stops_the_background_apply_and_paints_normally()
    {
        var (svc, r, h) = Mounted();
        h.SetVisible(false); svc.Tick(0.2f);
        var refreshes = r.Refreshes; var applies = r.Applies;
        h.SetVisible(true); svc.Tick(0.2f); svc.Tick(0.2f);
        Assert.Equal(refreshes, r.Refreshes);
        Assert.True(r.Applies > applies);
    }
}
