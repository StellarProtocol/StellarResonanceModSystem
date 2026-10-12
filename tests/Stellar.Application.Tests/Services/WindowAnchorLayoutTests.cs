using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

/// <summary>
/// PINNED regression (framework 2.22.0 — never weaken). Owner report 2026-10-10 on MAIN: the Photo Studio free-camera
/// HUD (Anchor = Top) sat 220 px right of centre at 2560×1440. The saved layout held the absolute left edge of the
/// ORIGINAL 800 px window (x = 880) and every width change (800 → 1000 → 1240) kept that edge, so the centre drifted.
/// Owner: "make sure it support every resolution". An anchored window now saves its offset from its anchor point and is
/// re-placed from it at the CURRENT canvas, UI scale and width. The renderer below scales like the real CanvasScaler
/// (ScaleWithScreenSize, reference 2560×1440 ÷ UI scale, match 0.5).
/// </summary>
public sealed class WindowAnchorLayoutTests
{
    public static IEnumerable<object[]> Screens()
    {
        int[][] resolutions = { new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2560, 1440 }, new[] { 3840, 2160 }, new[] { 3440, 1440 } };
        float[] scales = { 0.8f, 1f, 1.25f };
        foreach (var r in resolutions)
            foreach (var u in scales)
                yield return new object[] { r[0], r[1], u };
    }

    [Theory]
    [InlineData(WindowAnchor.TopLeft)] [InlineData(WindowAnchor.Top)] [InlineData(WindowAnchor.TopRight)]
    [InlineData(WindowAnchor.Left)] [InlineData(WindowAnchor.Center)] [InlineData(WindowAnchor.Right)]
    [InlineData(WindowAnchor.BottomLeft)] [InlineData(WindowAnchor.Bottom)] [InlineData(WindowAnchor.BottomRight)]
    public void Place_inverts_OffsetOf_for_every_anchor(WindowAnchor anchor)
    {
        var canvas = new CanvasDims(1706.7f, 960f, 1.25f);
        var rect = new WindowRect(123f, 45f, 640f, 74f);
        var back = AnchoredPlacement.Place(anchor, AnchoredPlacement.OffsetOf(anchor, rect, canvas), (rect.Width, rect.Height), canvas);
        Assert.Equal(rect.X, back.X, 3);
        Assert.Equal(rect.Y, back.Y, 3);
    }

    // The owner's case, at every resolution and UI scale: saved while 1000 wide and centred, restored 1240 wide.
    [Theory]
    [MemberData(nameof(Screens))]
    public void A_centred_top_window_stays_centred_after_its_width_changes(int w, int h, float u)
    {
        var rig = new Rig(w, h, u);
        rig.MountAndAutoSave(Hud(1000f));   // first mount auto-saves (the content-sized height differs from 0)
        var placed = rig.Restore(Hud(1240f));
        AssertCentred(rig, placed, 1240f);
    }

    // A layout saved at one resolution and reused at a nearby one (LayoutStorage's closest-resolution fallback).
    [Fact]
    public void A_centred_window_saved_at_one_resolution_is_centred_at_a_nearby_one()
    {
        var rig = new Rig(2560, 1440, 1f);
        rig.MountAndAutoSave(Hud(1000f));
        rig.SetScreen(2560, 1600, 1f);
        var placed = rig.Restore(Hud(1240f));
        AssertCentred(rig, placed, 1240f);
    }

    // A deliberate placement keeps its distance from the anchor (design units), not its absolute left edge.
    [Theory]
    [MemberData(nameof(Screens))]
    public void A_dragged_window_keeps_its_offset_from_centre_after_its_width_changes(int w, int h, float u)
    {
        var rig = new Rig(w, h, u);
        var handle = rig.MountAndAutoSave(Hud(1000f));
        var c = rig.Canvas;
        handle.SetRect(new WindowRect((c.Width - 1000f) / 2f + 100f / u, 40f, 1000f, 74f));   // +100 design units right
        var placed = rig.Restore(Hud(1240f));
        Assert.Equal(1240f, placed.Width, 1);   // placed at the CURRENT width, not the saved one
        Assert.Equal(c.Width / 2f + 100f / u, placed.X + placed.Width / 2f, 1);
        Assert.Equal(40f, placed.Y, 1);
    }

    // A save from before 2.22.0 (no offset) for THIS resolution keeps the anchor point it had (not its left edge).
    [Fact]
    public void A_legacy_exact_resolution_save_keeps_its_centre_after_a_width_change()
    {
        var rig = new Rig(2560, 1440, 1f);
        var c = rig.Canvas;
        rig.Storage.Save(rig.Storage.ActiveSlot, "hud", rig.Screen, new WindowRect((c.Width - 1000f) / 2f, 16f, 1000f, 74f), true);
        var placed = rig.Restore(Hud(1240f));
        AssertCentred(rig, placed, 1240f);
    }

    [Fact]
    public void A_top_right_window_keeps_its_distance_from_the_right_edge()
    {
        var rig = new Rig(1920, 1080, 1f);
        var handle = rig.MountAndAutoSave(Hud(400f, WindowAnchor.TopRight));
        var c = rig.Canvas;
        handle.SetRect(new WindowRect(c.Width - 400f - 30f, 10f, 400f, 74f));   // 30 px in from the right edge
        var placed = rig.Restore(Hud(520f, WindowAnchor.TopRight));
        Assert.Equal(520f, placed.Width, 1);
        Assert.Equal(c.Width - 30f, placed.X + placed.Width, 1);
    }

    // Rollback-safe (process rules § 6): the absolute rect keys are still written; a later save without an offset
    // clears the old one (anc = false) instead of leaving it to win on the next boot.
    [Fact]
    public void The_offset_rides_beside_the_old_keys_and_an_anchorless_save_clears_it()
    {
        var rig = new Rig(2560, 1440, 1f);
        rig.MountAndAutoSave(Hud(1000f));
        var section = rig.Config.Section("ui.layout");
        const string key = "slots.0.windows.hud.2560x1440";
        Assert.True(section.Has(key + ".x") && section.Has(key + ".w"));
        Assert.True(section.Get(key + ".anc", false));
        rig.Storage.Save(rig.Storage.ActiveSlot, "hud", rig.Screen, new WindowRect(5f, 5f, 1000f, 74f), true);
        Assert.False(section.Get(key + ".anc", true));
        var reread = new LayoutStorage(rig.Config, new NullLog());
        Assert.False(reread.TryGetAnchorOffset(0, "hud", rig.Screen, out _, out var legacy));
        Assert.True(legacy);
    }

    private static void AssertCentred(Rig rig, WindowRect placed, float width)
    {
        Assert.Equal(width, placed.Width, 1);
        Assert.Equal(rig.Canvas.Width / 2f, placed.X + placed.Width / 2f, 1);
    }

    private static WindowRegistration Hud(float width, WindowAnchor anchor = WindowAnchor.Top) =>
        new(new WindowSpec("hud", "hud", new WindowRect(0f, 16f, width, 0f), WindowCategory.HUD, WindowPanelStyle.Borderless)
            { Anchor = anchor, Draggable = true, EditModeDragOnly = true, ShouldRender = () => true }, new TextElement(() => "hud"));

    private sealed class Rig
    {
        public readonly ScaledRenderer Renderer = new();
        public readonly InMemoryConfig Config = new();
        public readonly LayoutStorage Storage;
        public Resolution Screen;

        public Rig(int w, int h, float u)
        {
            Storage = new LayoutStorage(Config, new NullLog());
            SetScreen(w, h, u);
        }

        public CanvasDims Canvas => new(Screen.Width / Renderer.CanvasScale, Screen.Height / Renderer.CanvasScale, Renderer.UiScale);

        public void SetScreen(int w, int h, float u)
        {
            Screen = new Resolution(w, h);
            Renderer.UiScale = u;
            Renderer.CanvasScale = u * MathF.Sqrt(w / 2560f * (h / 1440f));
        }

        /// <summary>Mounts once; the renderer then reports the content-sized height, which the next tick persists.</summary>
        public IWindowControl MountAndAutoSave(WindowRegistration reg)
        {
            var svc = new WindowService(Renderer, new NullLog());
            svc.AttachLayout(Storage, () => Screen);
            var handle = svc.Register(reg);
            svc.Tick(0.2f);
            Renderer.Last = Renderer.Last with { Height = 74f };
            svc.Tick(0.2f);
            svc.Tick(0.2f);
            return handle;
        }

        /// <summary>A fresh boot with <paramref name="reg"/> (same id): where the window is placed.</summary>
        public WindowRect Restore(WindowRegistration reg)
        {
            var svc = new WindowService(Renderer, new NullLog());
            svc.AttachLayout(Storage, () => Screen);
            svc.Register(reg);
            svc.Tick(0.2f);
            return Renderer.Last;
        }
    }

    private sealed class ScaledRenderer : IWindowRenderer, IWindowCanvasMetrics
    {
        public WindowRect Last;
        public bool IsCanvasAvailable() => true;
        public object? Mount(WindowRegistration reg) => new object();
        public bool IsAlive(object? token) => token != null;
        public void ApplyValues(object? token, WindowRegistration reg, bool hide) { }
        public void SetRect(object? token, WindowRect rect) => Last = rect;
        public WindowRect GetRect(object? token) => Last;
        public bool HasFocusedField(object? token) => false;
        public void Destroy(object? token) { }
        public float CanvasScale { get; set; } = 1f;
        public float UiScale { get; set; } = 1f;
        public bool CanvasScaleReady => true;
        public int CanvasGeneration => 1;
    }

    private sealed class NullLog : IPluginLog
    { public void Info(string m){} public void Warning(string m){} public void Error(string m){} public void Debug(string m){} }

    private sealed class InMemoryConfig : IPluginConfig
    {
        private readonly Dictionary<string, InMemorySection> _sections = new();
        public InMemorySection Section(string name) => (InMemorySection)GetSection(name);
#pragma warning disable CS0067
        public event Action<string>? SectionChanged;
#pragma warning restore CS0067
        public IConfigSection GetSection(string name)
        {
            if (!_sections.TryGetValue(name, out var s)) { s = new InMemorySection(); _sections[name] = s; }
            return s;
        }
    }

    private sealed class InMemorySection : IConfigSection
    {
        private readonly Dictionary<string, object?> _store = new();
        public bool Has(string key) => _store.ContainsKey(key);
        public T? Get<T>(string key, T? defaultValue) => _store.TryGetValue(key, out var v) && v is T t ? t : defaultValue;
        public void Set<T>(string key, T value) => _store[key] = value;
        public void Save() { }
        public void SaveQuiet() { }
        public void RemoveByPrefix(string prefix)
        {
            foreach (var k in new List<string>(_store.Keys))
                if (k.StartsWith(prefix, StringComparison.Ordinal)) _store.Remove(k);
        }
    }
}
