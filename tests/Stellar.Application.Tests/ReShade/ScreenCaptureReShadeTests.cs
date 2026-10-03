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
        public int FailAtScale = -1;

        public Task<FrameGrab> GrabAsync(GrabTarget target, int settleFrames, CaptureFormat format, int jpgQuality)
        {
            Targets.Add(target);
            var scale = Math.Max(target.Size.Width, target.Size.Height) / 4;
            if (scale == FailAtScale) throw new FrameGrabException("oom");
            int w = target.Size.Width, h = target.Size.Height;
            return Task.FromResult(new FrameGrab(new byte[w * h * 4], w, h, null, Note));
        }

        public Task ResumeOnMainThreadAsync() => Task.CompletedTask;
    }

    private sealed class FakeReShade : IReShade
    {
        public bool IsAvailable { get; set; } = true;
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

    [Fact]
    public async Task Unavailable_passes_nothing()
    {
        await Service(new FakeReShade { IsAvailable = false }).CaptureAsync(Request());
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
        var service = new ReShadeService(native, new EffectDepthIndex(new InMemoryEffectFiles()), new NullLog());
        service.Refresh();
        native.NextFrame();
        service.Refresh();

        Assert.False(service.IsAvailable);
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
        var service = new ReShadeService(native, new EffectDepthIndex(new InMemoryEffectFiles()), new NullLog());
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
        var reShade = new LiveReadReShade { IsAvailable = false };
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

    private sealed class LiveReadReShade : IReShade, IReShadeLiveRead
    {
        public int RefreshCount;
        public bool IsAvailable { get; set; } = true;
        public bool Enabled { get; set; } = true;
        public IReadOnlyList<ReShadeTechnique> Techniques { get; } = new[] { new ReShadeTechnique("Bloom", "Bloom.fx", true, false) };
        public string? CurrentPreset => null;
        public void RefreshNow() { RefreshCount++; IsAvailable = true; }
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
