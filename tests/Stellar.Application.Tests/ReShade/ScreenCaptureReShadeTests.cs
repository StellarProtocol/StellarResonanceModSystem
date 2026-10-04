using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
using Stellar.Application.Services;
using Stellar.Infrastructure.Rendering;
using Xunit;

namespace Stellar.Application.Tests.ReShade;

// ScreenCaptureService hands the grabber ReShade options ONLY when the request asks for ReShade AND ReShade is available
// AND its effects are on (and something is active to draw). Anything else must be the pre-ReShade capture, unchanged.
public sealed class ScreenCaptureReShadeTests : IDisposable
{
    private sealed class Grabber : IFrameGrabber
    {
        public (int Width, int Height) ScreenSize => (4, 2);
        public int MaxTextureSize => 16384;
        public readonly List<GrabTarget> Targets = new();
        public string? Note;
        public string? GuardNote;
        public int FailAtScale = -1;

        public Task<FrameGrab> GrabAsync(GrabTarget target, int settleFrames, CaptureFormat format, int jpgQuality)
        {
            Targets.Add(target);
            var scale = Math.Max(target.Size.Width, target.Size.Height) / 4;
            if (scale == FailAtScale) throw new FrameGrabException("oom");
            int w = target.Size.Width, h = target.Size.Height;
            return Task.FromResult(new FrameGrab(new byte[w * h * 4], w, h, null, Note) { GuardNote = GuardNote });
        }

        public Task ResumeOnMainThreadAsync() => Task.CompletedTask;
    }

    private sealed class FakeReShade : IReShade
    {
        public ReShadeState State { get; set; } = ReShadeState.Ready;
        public bool Enabled { get; set; } = true;
        public List<ReShadeTechnique> List { get; } = new()
        {
            new ReShadeTechnique("Bloom", "Bloom.fx", true, false),
            new ReShadeTechnique("Off", "Off.fx", false, false),
            new ReShadeTechnique("MXAO", "MXAO.fx", true, true),
        };
        public IReadOnlyList<ReShadeTechnique> Techniques => List;
        public string? CurrentPreset => null;
        public void SetTechnique(string effectFile, string name, bool enabled) { }
        public void SetPreset(string path) { }
        public void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders) { }
        public event Action? Changed { add { } remove { } }
    }

    private sealed class NoVisibility : ISceneVisibility
    {
        public IDisposable Hide(VisibilityLayers layers) => new Token();
        public VisibilityLayers Hidden => VisibilityLayers.None;
        public VisibilityLayers Available => VisibilityLayers.None;
        public event Action<VisibilityLayers>? Changed { add { } remove { } }
        private sealed class Token : IDisposable { public void Dispose() { } }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-capture-" + Guid.NewGuid().ToString("N"));
    private readonly Grabber _grabber = new();

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch { /* best-effort cleanup */ }
    }

    private ScreenCaptureService Service(IReShade? reShade) =>
        new(_grabber, new NoVisibility(), new CaptureFileSink(), _ => { }, null, reShade);

    private CaptureRequest Request(bool apply = true, CaptureAspect? aspect = null) =>
        new() { Scale = 2, Directory = _dir, FileStem = "p", ApplyReShade = apply, Aspect = aspect };

    [Fact]
    public async Task Available_enabled_and_requested_passes_only_the_active_techniques()
    {
        var r = await Service(new FakeReShade()).CaptureAsync(Request());
        Assert.True(r.Success, r.Error);
        var options = Assert.Single(_grabber.Targets).ReShade;
        Assert.NotNull(options);
        Assert.False(options!.Shaped);
        Assert.Equal(new[] { "Bloom", "MXAO" }, options.Active.Select(t => t.Name));
    }

    [Fact]
    public async Task A_shaped_request_marks_the_options_shaped()
    {
        await Service(new FakeReShade()).CaptureAsync(Request(aspect: new CaptureAspect(9, 16)));
        var target = Assert.Single(_grabber.Targets);
        Assert.True(target.Shaped);
        Assert.True(target.ReShade!.Shaped);
    }

    [Fact]
    public async Task ApplyReShade_false_passes_nothing()
    {
        await Service(new FakeReShade()).CaptureAsync(Request(apply: false));
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
    }

    [Theory]
    [InlineData(ReShadeState.NotInstalled)]
    [InlineData(ReShadeState.Loading)]
    public async Task Anything_but_Ready_passes_nothing(ReShadeState state)
    {
        await Service(new FakeReShade { State = state }).CaptureAsync(Request());
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
    }

    [Fact]
    public async Task Effects_off_passes_nothing()
    {
        await Service(new FakeReShade { Enabled = false }).CaptureAsync(Request());
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
    }

    [Fact]
    public async Task No_active_technique_passes_nothing()
    {
        var reShade = new FakeReShade();
        reShade.List.RemoveAll(t => t.Enabled);
        await Service(reShade).CaptureAsync(Request());
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
    }

    [Fact]
    public async Task No_ReShade_service_passes_nothing()
    {
        var r = await Service(null).CaptureAsync(Request());
        Assert.True(r.Success, r.Error);
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task The_grab_note_reaches_the_result()
    {
        _grabber.Note = ReShadeCaptureOptions.NotReadyNote;
        var r = await Service(new FakeReShade()).CaptureAsync(Request());
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { "ReShade was not ready — photo taken without it." }, r.Notes);
    }

    [Fact]
    public async Task The_2x_fallback_keeps_the_options()
    {
        _grabber.FailAtScale = 4;
        var r = await Service(new FakeReShade()).CaptureAsync(Request() with { Scale = 4 });
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, _grabber.Targets.Count);
        Assert.All(_grabber.Targets, t => Assert.NotNull(t.ReShade));
    }

    // No ReShade installed (or a vanilla launch): the add-on module is absent, the real service stays unavailable, and
    // the capture is exactly the pre-ReShade one — no options, no note.
    [Fact]
    public async Task Not_installed_is_a_no_op_through_the_real_service()
    {
        var native = new FakeReShadeNative { IsLoaded = false };
        native.Add("Bloom", "Bloom.fx");
        var service = new ReShadeService(native, new EffectDepthIndex(new InMemoryEffectFiles()), new EffectSizeLockIndex(new InMemoryEffectFiles()), new EffectTemporalIndex(new InMemoryEffectFiles()), new NullLog());
        service.Refresh();
        native.NextFrame();
        service.Refresh();

        Assert.Equal(ReShadeState.NotInstalled, service.State);
        Assert.False(service.Enabled);
        var r = await Service(service).CaptureAsync(Request());
        Assert.True(r.Success, r.Error);
        Assert.Null(Assert.Single(_grabber.Targets).ReShade);
        Assert.Empty(r.Notes);
        Assert.Empty(native.Requests);
    }

    // Item 6: a depth effect switched on a moment before the shutter (no frame presented / inside the re-read interval,
    // so the per-tick poll has not seen it) must still be in the photo's plan — the capture forces a re-read first.
    [Fact]
    public async Task A_technique_enabled_just_before_the_shutter_is_in_the_plan()
    {
        var native = new FakeReShadeNative();
        native.Add("Bloom", "Bloom.fx");
        native.Add("MXAO", "MXAO.fx", enabled: false);
        var service = new ReShadeService(native, new EffectDepthIndex(new InMemoryEffectFiles()), new EffectSizeLockIndex(new InMemoryEffectFiles()), new EffectTemporalIndex(new InMemoryEffectFiles()), new NullLog());
        service.Refresh();
        Assert.Equal(new[] { "Bloom" }, service.Techniques.Where(t => t.Enabled).Select(t => t.Name));

        native.Techniques[1] = ("MXAO", "MXAO.fx", true);   // same frame counter: a plain Refresh() would return early
        service.Refresh();
        Assert.False(service.Techniques.Single(t => t.Name == "MXAO").Enabled);

        await Service(service).CaptureAsync(Request(aspect: new CaptureAspect(1, 1)));
        var options = Assert.Single(_grabber.Targets).ReShade!;
        var mxao = Assert.Single(options.Active, t => t.Name == "MXAO");
        Assert.True(mxao.UsesDepth);   // not on any search path / not yet resolved -> counts as depth (D8-safe)
    }

    [Fact]
    public async Task The_live_read_happens_before_availability_is_checked()
    {
        var reShade = new LiveReadReShade { State = ReShadeState.Loading };
        await Service(reShade).CaptureAsync(Request());
        Assert.Equal(1, reShade.RefreshCount);
        Assert.NotNull(Assert.Single(_grabber.Targets).ReShade);   // RefreshNow made it available
    }

    [Fact]
    public async Task No_live_read_when_the_request_does_not_want_ReShade()
    {
        var reShade = new LiveReadReShade();
        await Service(reShade).CaptureAsync(Request(apply: false));
        Assert.Equal(0, reShade.RefreshCount);
    }

    // ---- Fix 1 (2026-10-04): ReShade creates a screen-sized texture once and reuses it for every other size, so a 4x
    // render of an effect that declares one reads its top-left quarter magnified (AcerolaFX Draft: a 256x hatch).

    private static FakeReShade WithLocked()
    {
        var reShade = new FakeReShade();
        reShade.List[0] = reShade.List[0] with { SizeLocked = true };   // Bloom
        return reShade;
    }

    // 2026-10-05 owner decision: such a photo is drawn in a SEPARATE ReShade runtime of the photo's size (bridge 1.1.0
    // isolated capture). The service plans it; the grabber tries it and, on any failure (or a 1.0.0 bridge), takes the
    // planned fallback — the 1x / skip guard above — whose note it attaches.

    [Fact]
    public async Task A_size_locked_effect_plans_an_isolated_4x_photo_with_the_1x_guard_as_fallback()
    {
        var r = await Service(WithLocked()).CaptureAsync(Request() with { Scale = 4 });
        Assert.True(r.Success, r.Error);
        var target = Assert.Single(_grabber.Targets);
        Assert.Equal(new CaptureSize(16, 8), target.Size);   // the photo's own size
        var isolated = Assert.IsType<IsolatedCapture>(target.ReShade!.Isolated);
        Assert.Equal(new CaptureSize(4, 2), isolated.Fallback.Size);   // the screen size
        Assert.False(isolated.Fallback.Shaped);
        Assert.Null(isolated.Fallback.ReShade!.Isolated);
        Assert.False(isolated.Fallback.ReShade.SkipSizeLocked);
        Assert.Equal(ReShadeCaptureNotes.ScreenSizeOnly, isolated.FallbackNote);
        Assert.Empty(r.Notes);   // the fake grabber "succeeded" isolated: no note from the service itself
    }

    [Fact]
    public async Task A_shaped_photo_plans_isolated_with_the_skip_guard_as_fallback()
    {
        await Service(WithLocked()).CaptureAsync(Request(aspect: new CaptureAspect(1, 1)));
        var target = Assert.Single(_grabber.Targets);
        Assert.Equal(new CaptureSize(8, 8), target.Size);
        var isolated = target.ReShade!.Isolated!;
        Assert.Equal(new CaptureSize(8, 8), isolated.Fallback.Size);
        Assert.True(isolated.Fallback.Shaped);
        Assert.True(isolated.Fallback.ReShade!.SkipSizeLocked);
        Assert.Null(isolated.Fallback.ReShade.Isolated);
        Assert.Equal(ReShadeCaptureNotes.ScreenSizeOnlySkipped, isolated.FallbackNote);
    }

    [Fact]
    public async Task Only_unlocked_effects_keep_the_game_runtime_path_at_4x()
    {
        var r = await Service(new FakeReShade()).CaptureAsync(Request() with { Scale = 4 });
        var target = Assert.Single(_grabber.Targets);
        Assert.Equal(new CaptureSize(16, 8), target.Size);
        Assert.Null(target.ReShade!.Isolated);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task A_size_locked_effect_that_is_switched_off_does_not_go_isolated()
    {
        var reShade = new FakeReShade();
        reShade.List[1] = reShade.List[1] with { SizeLocked = true };   // "Off": not enabled
        await Service(reShade).CaptureAsync(Request() with { Scale = 4 });
        Assert.Null(Assert.Single(_grabber.Targets).ReShade!.Isolated);
    }

    [Fact]
    public async Task A_screen_sized_photo_with_a_size_locked_effect_stays_on_the_game_runtime()
    {
        var r = await Service(WithLocked()).CaptureAsync(Request() with { Scale = 1 });
        var target = Assert.Single(_grabber.Targets);
        Assert.Equal(new CaptureSize(4, 2), target.Size);
        Assert.Null(target.ReShade!.Isolated);
        Assert.Empty(r.Notes);
    }

    [Fact]
    public async Task ApplyReShade_false_plans_nothing()
    {
        await Service(WithLocked()).CaptureAsync(Request(apply: false) with { Scale = 4 });
        var target = Assert.Single(_grabber.Targets);
        Assert.Equal(new CaptureSize(16, 8), target.Size);
        Assert.Null(target.ReShade);
    }

    [Fact]
    public async Task The_fallback_note_and_the_grab_note_both_reach_the_result()
    {
        _grabber.GuardNote = ReShadeCaptureNotes.ScreenSizeOnly;
        _grabber.Note = ReShadeCaptureNotes.DrewNothing;
        var r = await Service(WithLocked()).CaptureAsync(Request() with { Scale = 4 });
        Assert.Equal(new[] { ReShadeCaptureNotes.ScreenSizeOnly, ReShadeCaptureNotes.DrewNothing }, r.Notes);
    }

    [Fact]
    public async Task The_4x_to_2x_retry_plans_isolated_again_at_2x()
    {
        _grabber.FailAtScale = 4;
        var r = await Service(WithLocked()).CaptureAsync(Request() with { Scale = 4 });
        Assert.True(r.Success, r.Error);
        Assert.Equal(2, _grabber.Targets.Count);
        Assert.Equal(new CaptureSize(8, 4), _grabber.Targets[1].Size);
        Assert.Equal(new CaptureSize(4, 2), _grabber.Targets[1].ReShade!.Isolated!.Fallback.Size);
    }

    // Warm-up before the isolated photo only when an ACTIVE effect is temporal; unknown (no traits) = warm up.
    private sealed class TraitsReShade : IReShade, IReShadeEffectTraits
    {
        public readonly HashSet<string> Temporal = new();
        public ReShadeState State => ReShadeState.Ready;
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ReShadeTechnique> Techniques { get; } = new[]
        {
            new ReShadeTechnique("Draft", "Draft.fx", true, false) { SizeLocked = true },
            new ReShadeTechnique("Bloom", "Bloom.fx", true, false),
            new ReShadeTechnique("Adapt", "Adapt.fx", false, false),
        };
        public string? CurrentPreset => null;
        public bool IsTemporal(string effectFile) => Temporal.Contains(effectFile);
        public void SetTechnique(string effectFile, string name, bool enabled) { }
        public void SetPreset(string path) { }
        public void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders) { }
        public event Action? Changed { add { } remove { } }
    }

    [Fact]
    public async Task An_isolated_photo_warms_up_when_an_active_effect_is_temporal()
    {
        var reShade = new TraitsReShade();
        reShade.Temporal.Add("Bloom.fx");
        await Service(reShade).CaptureAsync(Request() with { Scale = 4 });
        Assert.True(Assert.Single(_grabber.Targets).ReShade!.Isolated!.WarmUp);
    }

    [Fact]
    public async Task An_isolated_photo_skips_the_warm_up_when_no_active_effect_is_temporal()
    {
        var reShade = new TraitsReShade();
        reShade.Temporal.Add("Adapt.fx");   // temporal, but switched off
        await Service(reShade).CaptureAsync(Request() with { Scale = 4 });
        Assert.False(Assert.Single(_grabber.Targets).ReShade!.Isolated!.WarmUp);
    }

    [Fact]
    public async Task Without_effect_traits_an_isolated_photo_always_warms_up()
    {
        await Service(WithLocked()).CaptureAsync(Request() with { Scale = 4 });
        Assert.True(Assert.Single(_grabber.Targets).ReShade!.Isolated!.WarmUp);
    }

    private sealed class LiveReadReShade : IReShade, IReShadeLiveRead
    {
        public int RefreshCount;
        public ReShadeState State { get; set; } = ReShadeState.Ready;
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ReShadeTechnique> Techniques { get; } = new[] { new ReShadeTechnique("Bloom", "Bloom.fx", true, false) };
        public string? CurrentPreset => null;
        public void RefreshNow() { RefreshCount++; State = ReShadeState.Ready; }
        public void SetTechnique(string effectFile, string name, bool enabled) { }
        public void SetPreset(string path) { }
        public void SetSearchPaths(IReadOnlyList<string> effectFolders, IReadOnlyList<string> textureFolders) { }
        public event Action? Changed { add { } remove { } }
    }

    private sealed class NullLog : IPluginLog
    {
        public void Debug(string message) { }
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }
}
