using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// When WindowService re-activates a window (= puts it on top, WindowStacking): a REAL show from hidden of a parked
// window does; a layout visibility restore (ReapplyLayout / ApplySavedRect) does not; Topmost set before the mount is
// applied at the mount; BringToFront before the mount is applied at the mount.
public sealed class WindowActivationTests
{
    private static readonly Resolution Res = new(2560, 1440);

    private sealed class Renderer : IWindowRenderer, IWindowOrder, IWindowParking
    {
        public readonly List<string> Calls = new();
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) { Calls.Add("mount"); return 1; }
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) { }
        public WindowRect GetRect(object? token) => new(10, 10, 100, 100);
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) => Calls.Add("destroy");
        public void BringToFront(object? token) => Calls.Add("front");
        public void Activate(object? token) => Calls.Add("activate");
        public void SetTopmost(object? token, bool topmost) => Calls.Add("topmost:" + topmost);
        public void Park(object? token) => Calls.Add("park");
        public void Unpark(object? token) => Calls.Add("unpark");
    }

    private static (WindowService svc, Renderer r, IWindowControl h) Rig(bool topmostSpec = false)
    {
        var r = new Renderer();
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(new LayoutStorage(new Config(), new NullLog()), () => Res);
        var spec = new WindowSpec("w", "", new WindowRect(10, 10, 100, 100), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { StartVisible = false, ShouldRender = () => true, Topmost = topmostSpec };
        return (svc, r, svc.Register(new WindowRegistration(spec, new TextElement(() => "x"))));
    }

    [Fact]
    public void Real_show_from_hidden_reactivates_a_parked_window()
    {
        var (svc, r, h) = Rig();
        h.SetVisible(true); svc.Tick(0.2f);    // first mount = an activation by itself; no extra Activate
        Assert.DoesNotContain("activate", r.Calls);
        h.SetVisible(false); svc.Tick(0.2f);   // park
        r.Calls.Clear();
        h.SetVisible(true); svc.Tick(0.2f);
        Assert.Equal(new[] { "unpark", "activate" }, r.Calls.ToArray());
    }

    [Fact]
    public void Layout_visibility_restore_unparks_without_reactivating()
    {
        var (svc, r, h) = Rig();
        h.SetVisible(true); svc.Tick(0.2f);
        h.SetVisible(false); svc.Tick(0.2f);
        r.Calls.Clear();
        svc.ReapplyLayout(applyVisibility: true);   // saved state says shown (nothing persisted → default visible)
        svc.Tick(0.2f);
        Assert.Contains("unpark", r.Calls);
        Assert.DoesNotContain("activate", r.Calls);
    }

    [Fact]
    public void Hide_and_show_within_one_tick_does_not_reactivate()
    {
        var (svc, r, h) = Rig();
        h.SetVisible(true); svc.Tick(0.2f);
        r.Calls.Clear();
        h.SetVisible(false); h.SetVisible(true); svc.Tick(0.2f);   // never actually hidden
        Assert.DoesNotContain("activate", r.Calls);
        h.SetVisible(false); svc.Tick(0.2f); h.SetVisible(true); svc.Tick(0.2f);
        Assert.Contains("activate", r.Calls);                     // the flag didn't linger and misfire, nor get lost
    }

    [Fact]
    public void Topmost_and_front_requested_before_mount_apply_at_mount()
    {
        var (svc, r, h) = Rig();
        h.SetTopmost(true);
        Assert.True(h.IsTopmost());
        h.SetVisible(true); h.BringToFront();
        svc.Tick(0.2f);
        Assert.Equal(new[] { "mount", "topmost:True", "front" }, r.Calls.ToArray());
    }

    [Fact]
    public void Spec_topmost_needs_no_runtime_call_and_can_be_cleared_live()
    {
        var (svc, r, h) = Rig(topmostSpec: true);
        Assert.True(h.IsTopmost());
        h.SetVisible(true); svc.Tick(0.2f);
        Assert.DoesNotContain("topmost:True", r.Calls);   // the renderer reads WindowSpec.Topmost at mount
        h.SetTopmost(false);
        Assert.Contains("topmost:False", r.Calls);
        Assert.False(h.IsTopmost());
    }

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
