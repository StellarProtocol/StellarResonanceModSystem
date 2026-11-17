using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Runtime resize limits (<see cref="IWindowSizeLimits"/>): kept on the window entry, pushed to the renderer
/// at once when mounted, re-applied on every (re)mount, normalised, idempotent, and ignored for non-resizable windows.</summary>
public sealed class WindowSizeLimitsTests
{
    private sealed class Renderer : IWindowRenderer, IWindowResizeLimits
    {
        public readonly List<(float MinW, float MinH, float MaxW, float MaxH)> Pushed = new();
        public readonly List<string> Calls = new();   // mount / limits / rect order
        public bool Alive = true;
        public void SetResizeLimits(object? token, float minW, float minH, float maxW, float maxH)
        { Pushed.Add((minW, minH, maxW, maxH)); Calls.Add("limits"); }
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) { Alive = true; Calls.Add("mount"); return reg.Spec.Id; }
        public bool IsAlive(object? token) => token != null && Alive;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) => Calls.Add("rect");
        public WindowRect GetRect(object? token) => new(0, 0, 500, 300);
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
    }

    private static WindowRegistration Reg(bool resizable = true) =>
        new(new WindowSpec("w", "w", new WindowRect(0, 0, 500, 300), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { ShouldRender = () => true, Resizable = resizable }, new TextElement(() => "x"));

    private static (WindowService svc, Renderer r, IWindowControl c) New(bool resizable = true)
    {
        var r = new Renderer();
        var svc = new WindowService(r, new Stellar.Application.Tests.NativeUi.NativeUiNullLog());
        var c = svc.Register(Reg(resizable));
        c.SetVisible(true);
        return (svc, r, c);
    }

    [Fact] // Set before the first mount → pushed at the mount, BEFORE the saved-rect restore (so the restore clamps).
    public void PreMountLimits_AppliedAtMount_BeforeRestore()
    {
        var (svc, r, c) = New();
        c.SetSizeLimits(560, 368, 760, 368);
        Assert.Empty(r.Pushed);                               // nothing mounted yet
        svc.Tick(0.2f);
        Assert.Equal(new[] { (560f, 368f, 760f, 368f) }, r.Pushed);
        var limits = r.Calls.IndexOf("limits");
        Assert.True(limits > r.Calls.IndexOf("mount"));
        Assert.True(limits < r.Calls.IndexOf("rect"));        // ApplySavedRect's SetRect comes after
    }

    [Fact] // Mounted → pushed at once; the same limits again are a no-op; a change is pushed.
    public void MountedLimits_PushedImmediately_Idempotent()
    {
        var (svc, r, c) = New();
        svc.Tick(0.2f);
        c.SetSizeLimits(500, 160, 760, 1000);
        c.SetSizeLimits(500, 160, 760, 1000);
        Assert.Single(r.Pushed);
        c.SetSizeLimits(560, 428, 760, 428);
        Assert.Equal(2, r.Pushed.Count);
        Assert.Equal((560f, 428f, 760f, 428f), r.Pushed[1]);
    }

    [Fact] // A remount (scene change / ShouldRender) rebuilds the grip from the spec → the live limits are re-applied.
    public void Remount_ReappliesLimits()
    {
        var (svc, r, c) = New();
        svc.Tick(0.2f);
        c.SetSizeLimits(560, 368, 760, 368);
        r.Alive = false;
        svc.Tick(0.2f);
        Assert.Equal(2, r.Pushed.Count);
        Assert.Equal((560f, 368f, 760f, 368f), r.Pushed[1]);
    }

    [Fact] // max below min is raised to min; negatives floor at 0.
    public void Limits_AreNormalised()
    {
        var (svc, r, c) = New();
        svc.Tick(0.2f);
        c.SetSizeLimits(-5, 400, 300, 100);
        Assert.Equal((0f, 400f, 300f, 400f), r.Pushed[0]);
    }

    [Fact] // No grip / size clamp on a non-resizable window → ignored.
    public void NonResizable_Ignored()
    {
        var (svc, r, c) = New(resizable: false);
        svc.Tick(0.2f);
        c.SetSizeLimits(10, 10, 20, 20);
        Assert.Empty(r.Pushed);
    }
}
