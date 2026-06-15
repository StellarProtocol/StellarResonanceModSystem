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

    [Fact]
    public void Register_InjectsOnlyWhenAnchorPresent()
    {
        var adapter = new FakeAdapter { Available = false };
        var svc = new UGuiInjectionService(adapter);
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
        var svc = new UGuiInjectionService(adapter);
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
        var svc = new UGuiInjectionService(adapter);
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
        var svc = new UGuiInjectionService(adapter);
        svc.Register(new IndicatorSpec(NativeUiAnchor.HudTopRight, () => "x"));
        svc.Tick(1f);
        svc.Tick(1f);
        Assert.True(adapter.ApplyCount >= 2);
    }

    [Fact]
    public void Tick_GatesWorkBelowInterval()
    {
        var adapter = new FakeAdapter { Available = true };
        var svc = new UGuiInjectionService(adapter);
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
        var svc = new UGuiInjectionService(adapter);
        svc.Register(Btn()); svc.Register(Btn());
        svc.Tick(1f);
        svc.OnFrameworkDispose();
        Assert.Equal(2, adapter.DestroyCount);
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
