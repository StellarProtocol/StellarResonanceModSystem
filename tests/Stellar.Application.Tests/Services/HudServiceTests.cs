using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public sealed class HudServiceTests
{
    private const float Step = 0.2f;   // > the 0.1s (10Hz) apply interval, so one Tick crosses it

    private static HudSpec Spec(string id) => new(id, HudAnchor.FreeOverlay, new TextElement(() => "x"));

    [Fact] public void MountsWhenAnchorAvailable()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a")); svc.Tick(Step); Assert.Equal(1, r.MountCount);
    }

    [Fact] public void DoesNotMountWhileAnchorAbsent_ThenMounts()
    {
        var r = new FakeRenderer { AnchorUp = false }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a")); svc.Tick(Step); Assert.Equal(0, r.MountCount);
        r.AnchorUp = true; svc.Tick(Step); Assert.Equal(1, r.MountCount);
    }

    [Fact] public void Applies_OncePerInterval_NotEveryTick()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a")); svc.Tick(Step);          // mount + first apply
        r.ApplyCount = 0;
        svc.Tick(0.05f);                                   // < interval → no apply
        Assert.Equal(0, r.ApplyCount);
        svc.Tick(0.05f);                                   // crosses 0.1s → apply
        Assert.Equal(1, r.ApplyCount);
    }

    [Fact] public void MarkDirty_AppliesImmediately_BeforeNextInterval()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        var h = svc.Register(Spec("a")); svc.Tick(Step); r.ApplyCount = 0;
        h.MarkDirty(); svc.Tick(0.001f); Assert.Equal(1, r.ApplyCount);
    }

    [Fact] public void SetVisibleFalse_DestroysAndIsNotShown()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        var h = svc.Register(Spec("a")); svc.Tick(Step);
        h.SetVisible(false); svc.Tick(Step);
        Assert.Equal(1, r.DestroyCount); Assert.False(h.IsShown);
    }

    [Fact] public void Remove_DestroysAndDoesNotRemount()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        var h = svc.Register(Spec("a")); svc.Tick(Step);
        h.Remove(); svc.Tick(Step); Assert.Equal(1, r.DestroyCount);
        r.MountCount = 0; svc.Tick(Step); Assert.Equal(0, r.MountCount);
    }

    [Fact] public void SelfHeals_WhenElementDiesUnexpectedly()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a")); svc.Tick(Step); r.Kill(); svc.Tick(Step);
        Assert.Equal(2, r.MountCount);
    }

    [Fact] public void AnchorLostAfterMount_GoesUnmounted_NoRemount()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        var h = svc.Register(Spec("a")); svc.Tick(Step);   // mounts
        var mountsBefore = r.MountCount;
        r.AnchorUp = false; r.Kill();                       // anchor gone + element dead
        svc.Tick(Step);
        Assert.Equal(mountsBefore, r.MountCount);           // no remount
        Assert.False(h.IsShown);
    }

    [Fact] public void DuplicateId_SecondRegister_ReturnsInertHandle()
    {
        var r = new FakeRenderer { AnchorUp = true }; var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a"));
        var dup = svc.Register(Spec("a")); svc.Tick(Step);
        Assert.False(dup.IsShown);
        Assert.Equal(1, r.MountCount);
    }

    [Fact] public void Mount_AppliesSavedRect()
    {
        var r = new FakeRenderer { AnchorUp = true }; var st = NewStorage(); var res = new Resolution(1920,1080);
        st.Save(st.ActiveSlot, "a", res, new WindowRect(500,300,200,40), true);
        var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true }); svc.AttachLayout(st, () => res);
        svc.Register(Spec("a")); svc.Tick(Step);
        Assert.Equal(new WindowRect(500,300,200,40), r.LastSetRect);
    }
    [Fact] public void CommitRect_Persists()
    {
        var r = new FakeRenderer { AnchorUp = true }; var st = NewStorage(); var res = new Resolution(1920,1080);
        var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true }); svc.AttachLayout(st, () => res);
        svc.Register(Spec("a")); svc.Tick(Step);
        svc.BeginDrag("a"); svc.SetRect("a", new WindowRect(800,600,200,40)); svc.CommitRect("a");
        var (rect,_) = st.Get(st.ActiveSlot, "a", res, new WindowRect(0,0,1,1));
        Assert.Equal(new WindowRect(800,600,200,40), rect);
    }
    [Fact] public void SelfHeal_DoesNotStompActiveDrag()
    {
        var r = new FakeRenderer { AnchorUp = true }; var st = NewStorage(); var res = new Resolution(1920,1080);
        st.Save(st.ActiveSlot, "a", res, new WindowRect(10,10,200,40), true);
        var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true }); svc.AttachLayout(st, () => res);
        svc.Register(Spec("a")); svc.Tick(Step);
        svc.BeginDrag("a"); r.Kill(); r.LastSetRect = default; svc.Tick(Step);   // re-mount mid-drag
        Assert.NotEqual(new WindowRect(10,10,200,40), r.LastSetRect);                   // saved rect NOT re-applied
        svc.EndDrag("a");
    }
    [Fact] public void AutoHidesBehindFullScreenMenu()
    {
        var r = new FakeRenderer { AnchorUp = true }; var menu = new StubMenuState();
        var svc = new HudService(r, new NullLog(), menu, new StubClientState { IsLoggedIn = true });
        svc.Register(Spec("a"));                       // HudSpec default AutoHideBehindGameMenus = true
        svc.Tick(Step); Assert.False(r.LastHide);      // menu closed → shown
        menu.IsFullScreenMenuOpen = true; svc.Tick(Step);
        Assert.True(r.LastHide);                       // menu open → hidden
    }

    [Fact] // Hidden HUD is still enumerated as editable, with Visible=false and its last-known rect.
    public void EditableElements_IncludesHidden_WithLastRect()
    {
        var r = new FakeRenderer { AnchorUp = true };
        var svc = new HudService(r, new NullLog(), new StubMenuState(), new StubClientState { IsLoggedIn = true });
        svc.AttachLayout(NewStorage(), () => new Resolution(1920, 1080));
        svc.Register(Spec("a")); svc.Tick(Step);                 // mount + apply; LastShownRect cached (100x40)

        svc.SetVisiblePersist("a", false); svc.Tick(Step);       // hide → destroyed

        var els = new List<EditableElement>(svc.EditableElements());
        var e = Assert.Single(els);
        Assert.Equal("a", e.Id);
        Assert.False(e.Visible);
        Assert.True(e.CanHide);
        Assert.Equal(100f, e.Rect.Width);                        // last-known rect, not default-empty
    }

    private static LayoutStorage NewStorage() => new(new InMemoryConfig(), new NullLog());

    private sealed class FakeRenderer : IHudRenderer
    {
        public bool AnchorUp; public int MountCount, ApplyCount, DestroyCount; private bool _alive;
        public WindowRect LastSetRect; private WindowRect _rect = new(0,0,100,40);
        public bool IsAnchorAvailable(HudAnchor a) => AnchorUp;
        public object? Mount(HudSpec s) { if (!AnchorUp) return null; MountCount++; _alive = true; return new object(); }
        public bool IsAlive(object? t) => _alive && t != null;
        public bool LastHide;
        public void ApplyValues(object? t, HudSpec s, bool hide) { ApplyCount++; LastHide = hide; }
        public void SetRect(object? t, WindowRect r) { LastSetRect = r; _rect = r; } public WindowRect GetRect(object? t) => _rect;
        public void Destroy(object? t) { DestroyCount++; _alive = false; } public void Kill() => _alive = false;
        public int TickAnimCount; public void TickAnimations(float dt) => TickAnimCount++;
    }

    private sealed class InMemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, InMemorySection> _sec = new();
#pragma warning disable CS0067
        public event Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sec.TryGetValue(name, out var s)) _sec[name] = s = new InMemorySection();
            return s;
        }
    }

    private sealed class InMemorySection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public T? Get<T>(string key, T? defaultValue)
            => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
    }
    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }
}
