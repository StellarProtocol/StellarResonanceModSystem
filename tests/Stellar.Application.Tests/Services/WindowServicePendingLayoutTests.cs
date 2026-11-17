using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>Plugin Rect/SetRect while the saved-layout restore is parked (WindowService.LayoutPending.cs). The bug:
/// a "keep position, change height" SetRect in that gap persisted the (20,20) mount placeholder and the deferred
/// restore then read it back — the window was moved to the top-left for good (CombatMeter, 2026-11-17).</summary>
public sealed class WindowServicePendingLayoutTests
{
    private static readonly Resolution Res = new(1920, 1080);

    // Renderer whose window really sits where SetRect put it, mounted at the UGuiPrimitives (20,20) placeholder,
    // with a toggleable "CanvasScaler settled" signal.
    private sealed class PlaceholderRenderer : IWindowRenderer, IWindowCanvasMetrics
    {
        public bool Ready;
        public WindowRect Live;
        public object? Mount(WindowRegistration reg) { Live = new WindowRect(20, 20, reg.Spec.DefaultRect.Width, reg.Spec.DefaultRect.Height); return 1; }
        public bool IsCanvasAvailable() => true;
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) => Live = rect;
        public WindowRect GetRect(object? token) => Live;
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
        public float CanvasScale => 1f;
        public float UiScale => 1f;
        public bool CanvasScaleReady => Ready;
        public int CanvasGeneration => 1;
    }

    private sealed class Rig
    {
        public readonly PlaceholderRenderer R = new();
        public readonly LayoutStorage Storage = new(new InMemoryConfig(), new NullLog());
        public readonly WindowService Svc;
        public Rig() { Svc = new WindowService(R, new NullLog()); Svc.AttachLayout(Storage, () => Res); }
        public WindowRect Saved(string id) => Storage.GetGlobal(id, Res, default, 1f).Rect;
    }

    private static WindowRegistration Reg(string id, bool hud = false) =>
        new(new WindowSpec(id, id, new WindowRect(100, 100, 300, 200), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => true, EditModeDragOnly = hud, Draggable = true }, new TextElement(() => "x"));

    [Fact] // The CombatMeter case: read Rect, write back same X/Y with a new height, before the restore lands.
    public void Resize_in_place_during_pending_keeps_saved_position_and_takes_new_size()
    {
        var rig = new Rig();
        rig.Storage.SaveGlobal("w", Res, new WindowRect(800, 500, 300, 200), visible: true);
        var h = rig.Svc.Register(Reg("w"));
        rig.Svc.Tick(0.2f);                                   // mount on an unsettled canvas → restore parked

        var r = h.Rect;
        Assert.Equal(800f, r.X); Assert.Equal(500f, r.Y);     // reports the restore target, not (20,20)
        h.SetRect(new WindowRect(r.X, r.Y, r.Width, 368));
        Assert.Equal(800f, rig.Saved("w").X);                 // nothing persisted while pending
        Assert.Equal(200f, rig.Saved("w").Height);
        Assert.Equal(368f, h.Rect.Height);                    // a follow-up read sees its own request

        rig.R.Ready = true; rig.Svc.Tick(0.2f);               // restore lands, queued resize re-issued on top
        Assert.Equal(new WindowRect(800, 500, 300, 368), rig.R.Live);
        Assert.Equal(new WindowRect(800, 500, 300, 368), rig.Saved("w"));
    }

    [Fact] // A plugin that captured the raw placeholder (old Rect) and echoes it is still treated as "keep position".
    public void Echo_of_the_placeholder_position_is_not_persisted()
    {
        var rig = new Rig();
        rig.Storage.SaveGlobal("w", Res, new WindowRect(800, 500, 300, 200), visible: true);
        var h = rig.Svc.Register(Reg("w"));
        rig.Svc.Tick(0.2f);
        h.SetRect(new WindowRect(20, 20, 300, 250));
        rig.R.Ready = true; rig.Svc.Tick(0.2f);
        Assert.Equal(new WindowRect(800, 500, 300, 250), rig.Saved("w"));
    }

    [Fact] // An explicit move (a position that is neither the preview nor the placeholder) wins in full.
    public void Explicit_move_during_pending_wins_after_restore()
    {
        var rig = new Rig();
        rig.Storage.SaveGlobal("w", Res, new WindowRect(800, 500, 300, 200), visible: true);
        var h = rig.Svc.Register(Reg("w"));
        rig.Svc.Tick(0.2f);
        h.SetRect(new WindowRect(400, 300, 300, 200));
        var r = h.Rect;                                       // then a resize-in-place of the moved rect
        h.SetRect(new WindowRect(r.X, r.Y, r.Width, 260));
        rig.R.Ready = true; rig.Svc.Tick(0.2f);
        Assert.Equal(new WindowRect(400, 300, 300, 260), rig.R.Live);
        Assert.Equal(new WindowRect(400, 300, 300, 260), rig.Saved("w"));
    }

    [Fact] // First-ever open (no saved layout): the preview is the spec default, and a resize keeps it.
    public void First_open_without_saved_layout_uses_default_rect()
    {
        var rig = new Rig();
        var h = rig.Svc.Register(Reg("w"));
        rig.Svc.Tick(0.2f);
        var r = h.Rect;
        Assert.Equal(100f, r.X); Assert.Equal(100f, r.Y);
        h.SetRect(new WindowRect(r.X, r.Y, r.Width, 240));
        rig.R.Ready = true; rig.Svc.Tick(0.2f);
        Assert.Equal(new WindowRect(100, 100, 300, 240), rig.Saved("w"));
    }

    [Fact] // An HUD (slot-keyed) window: same merge, written to the active slot.
    public void Slot_keyed_hud_window_merges_into_active_slot()
    {
        var rig = new Rig();
        rig.Storage.Save(rig.Storage.ActiveSlot, "hud", Res, new WindowRect(900, 40, 300, 200), visible: true);
        var h = rig.Svc.Register(Reg("hud", hud: true));
        rig.Svc.Tick(0.2f);
        var r = h.Rect;
        h.SetRect(new WindowRect(r.X, r.Y, 320, r.Height));
        rig.R.Ready = true; rig.Svc.Tick(0.2f);
        var saved = rig.Storage.Get(rig.Storage.ActiveSlot, "hud", Res, default, 1f).Rect;
        Assert.Equal(new WindowRect(900, 40, 320, 200), saved);
    }

    [Fact] // No race (canvas already settled): SetRect behaves exactly as before — applied and persisted at once.
    public void Settled_canvas_setrect_is_unchanged()
    {
        var rig = new Rig();
        rig.R.Ready = true;
        var h = rig.Svc.Register(Reg("w"));
        rig.Svc.Tick(0.2f);
        h.SetRect(new WindowRect(20, 20, 300, 200));          // even the placeholder spot is honoured when asked for
        Assert.Equal(new WindowRect(20, 20, 300, 200), rig.Saved("w"));
        Assert.Equal(new WindowRect(20, 20, 300, 200), h.Rect);
    }

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }

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
