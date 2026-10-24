using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Click-away popups (DismissOnOutsideClick) on their first mount of a session (ChatTools channel picker, 2026-10-24):
// the saved layout said visible=false (persisted by a SetRect issued while the popup was parked hidden), and the mount's
// ApplySavedRect restored it → the popup flashed at its old spot and hid itself. Pins: a popup's mount never restores a
// saved "hidden", and a SetRect issued before the mount is applied at the mount, over the saved rect.
public sealed class WindowPopupMountTests
{
    private static readonly Resolution Res = new(2560, 1440);

    private sealed class Renderer : IWindowRenderer
    {
        public int Mounts, Destroys; public WindowRect? Last;
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) { Mounts++; return 1; }
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) => Last = rect;
        public WindowRect GetRect(object? token) => Last ?? default;
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) => Destroys++;
    }

    private static WindowRegistration Reg(string id, bool popup) =>
        new(new WindowSpec(id, "", new WindowRect(100, 100, 150, 160), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { StartVisible = false, DismissOnOutsideClick = popup, ShouldRender = () => true },
            new TextElement(() => "x"), OnClose: () => { });

    private static (WindowService svc, Renderer r, IWindowControl h) Rig(bool popup)
    {
        var r = new Renderer();
        var storage = new LayoutStorage(new Config(), new NullLog());
        storage.SaveGlobal("p", Res, new WindowRect(62, 1036, 150, 160), visible: false);   // the stale saved "hidden"
        var svc = new WindowService(r, new NullLog());
        svc.AttachLayout(storage, () => Res);
        return (svc, r, svc.Register(Reg("p", popup)));
    }

    [Fact]
    public void Popup_first_open_stays_shown_and_lands_on_the_pre_mount_rect()
    {
        var (svc, r, h) = Rig(popup: true);
        var at = new WindowRect(40, 1200, 150, 160);

        h.SetRect(at);         // before the first mount (no token yet)
        h.SetVisible(true);
        svc.Tick(0.2f);        // mount
        svc.Tick(0.2f);        // a saved-hidden restore would park/destroy here

        Assert.True(h.IsShown);
        Assert.Equal(1, r.Mounts);
        Assert.Equal(0, r.Destroys);
        Assert.Equal(at, r.Last);
    }

    [Fact]
    public void Regular_window_still_restores_saved_visibility_on_mount()
    {
        var (svc, _, h) = Rig(popup: false);

        h.SetVisible(true);
        svc.Tick(0.2f);

        Assert.False(h.IsShown);   // unchanged behaviour: the saved "hidden" wins for a non-popup
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
