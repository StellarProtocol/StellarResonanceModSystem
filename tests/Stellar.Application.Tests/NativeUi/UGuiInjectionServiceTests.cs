using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.NativeUi;

public sealed class UGuiInjectionServiceTests
{
    private static MenuButtonSpec Btn(NativeUiAnchor a = NativeUiAnchor.MainMenuRail)
        => new(a, "Stellar", "gear", "tip", () => { });

    // The default anchors (MainMenuRail / HudTopRight) are relevant in World, so drive the service with a
    // World-phase client so the existing behavioural tests exercise the resolve/inject path.
    private static StubClientState World() => new() { Phase = GamePhase.World };

    [Fact]
    public void Register_InjectsOnlyWhenAnchorPresent()
    {
        var adapter = new FakeAdapter { Available = false };
        var svc = new UGuiInjectionService(adapter, World());
        var h = svc.Register(Btn());

        svc.Tick(1f);
        Assert.False(h.IsInjected);
        Assert.Equal(0, adapter.InjectCount);

        adapter.Available = true;
        svc.Tick(1f);
        Assert.True(h.IsInjected);
        Assert.Equal(1, adapter.InjectCount);
    }

    [Fact]
    public void Reinjects_AfterGameDestroysElement()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, World());
        svc.Register(Btn());
        svc.Tick(1f);
        Assert.Equal(1, adapter.InjectCount);

        adapter.AliveOverride = false;
        svc.Tick(1f);
        Assert.Equal(2, adapter.InjectCount);
    }

    [Fact]
    public void Remove_DestroysAndStopsReinjecting()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, World());
        var h = svc.Register(Btn());
        svc.Tick(1f);
        h.Remove();
        Assert.Equal(1, adapter.DestroyCount);
        adapter.AliveOverride = false;
        svc.Tick(1f);
        Assert.Equal(1, adapter.InjectCount);
        Assert.False(h.IsInjected);
    }

    [Fact]
    public void Tick_RefreshesContent_WhileAlive()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, World());
        svc.Register(new IndicatorSpec(NativeUiAnchor.HudTopRight, () => "x"));
        svc.Tick(1f);
        svc.Tick(1f);
        Assert.True(adapter.ApplyCount >= 2);
    }

    [Fact]
    public void Tick_GatesWorkBelowInterval()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, World());
        svc.Register(Btn());

        svc.Tick(0.05f); // below the ~0.2s gate — no work yet
        Assert.Equal(0, adapter.InjectCount);

        svc.Tick(0.2f);  // accumulated past the gate — injects once
        Assert.Equal(1, adapter.InjectCount);
    }

    [Fact]
    public void Dispose_DestroysAll()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, World());
        svc.Register(Btn()); svc.Register(Btn());
        svc.Tick(1f);
        svc.OnFrameworkDispose();
        Assert.Equal(2, adapter.DestroyCount);
    }

    [Fact]
    public void SkipsProbe_WhenAnchorNotRelevantForCurrentPhase()
    {
        // LoginSidebar is relevant ONLY at TitleScreen. In World the service must not probe/inject it at all
        // (zero GameObject.Find cost out-of-phase); when the phase becomes TitleScreen it injects.
        var adapter = new FakeAdapter { Available = true };
        var client = new StubClientState { Phase = GamePhase.World };
        var svc = new UGuiInjectionService(adapter, client);
        var h = svc.Register(Btn(NativeUiAnchor.LoginSidebar));

        svc.Tick(1f);
        Assert.False(h.IsInjected);
        Assert.Equal(0, adapter.InjectCount);   // skipped — not relevant in World

        client.Phase = GamePhase.TitleScreen;
        svc.Tick(1f);
        Assert.True(h.IsInjected);
        Assert.Equal(1, adapter.InjectCount);   // now relevant — injects
    }

    [Fact]
    public void SkipsProbe_ForInWorldAnchor_AtTitleScreen()
    {
        // Symmetric: the in-world MainMenuRail anchor must not probe at the title screen.
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter, new StubClientState { Phase = GamePhase.TitleScreen });
        var h = svc.Register(Btn(NativeUiAnchor.MainMenuRail));

        svc.Tick(1f);
        Assert.False(h.IsInjected);
        Assert.Equal(0, adapter.InjectCount);
    }

    private sealed class FakeAdapter : IUGuiCanvasAdapter
    {
        public bool Available;
        public int InjectCount, DestroyCount, ApplyCount;
        public bool AliveOverride = true;
        public bool IsAnchorAvailable(NativeUiAnchor a) => Available;
        public object? Inject(NativeUiElementSpec s) { if (!Available) return null; InjectCount++; return new object(); }
        public bool IsAlive(object? r) => r != null && AliveOverride;
        public void ApplyContent(object? r, NativeUiElementSpec s) => ApplyCount++;
        public void Destroy(object? r) => DestroyCount++;
    }
}
