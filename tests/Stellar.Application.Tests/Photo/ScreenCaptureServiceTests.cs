using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
using Stellar.Application.Services;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class ScreenCaptureServiceTests
{
    private sealed class FakeGrabber : IFrameGrabber
    {
        public (int Width, int Height) ScreenSize { get; set; } = (4, 2);
        public int FailAtScale = -1;
        public readonly List<(int scale, int settle)> Calls = new();
        public Func<bool>? HiddenDuringGrab;
        public bool SawHidden;
        public Task<FrameGrab> GrabAsync(int scale, int settleFrames, CaptureFormat format, int jpgQuality)
        {
            Calls.Add((scale, settleFrames));
            SawHidden = HiddenDuringGrab?.Invoke() ?? false;
            if (scale == FailAtScale) throw new FrameGrabException("oom");
            var w = ScreenSize.Width * scale; var h = ScreenSize.Height * scale;
            return Task.FromResult(new FrameGrab(new byte[w * h * 4], w, h, null));
        }
    }

    private sealed class FakeVisibility : ISceneVisibility
    {
        public int Live;
        public IDisposable Hide(VisibilityLayers layers) { Live++; return new D(() => Live--); }
        public VisibilityLayers Hidden => Live > 0 ? VisibilityLayers.GameHud : VisibilityLayers.None;
        public event Action<VisibilityLayers>? Changed { add { } remove { } }
        private sealed class D : IDisposable { private Action? _a; public D(Action a) => _a = a; public void Dispose() { _a?.Invoke(); _a = null; } }
    }

    private static string TempDir() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "photo-svc-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Hides_during_grab_and_restores_after()
    {
        var vis = new FakeVisibility();
        var g = new FakeGrabber();
        g.HiddenDuringGrab = () => vis.Live > 0;
        var s = new ScreenCaptureService(g, vis, new CaptureFileSink());
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 2, Directory = TempDir(), FileStem = "t", HideDuringCapture = VisibilityLayers.GameHud });
        Assert.True(r.Success, r.Error);
        Assert.True(g.SawHidden);
        Assert.Equal(0, vis.Live);
        Assert.Equal(2, g.Calls[0].settle);
        Assert.Equal((8, 4), (r.Width, r.Height));
    }

    [Fact]
    public async Task Four_x_failure_retries_once_at_two_x()
    {
        var g = new FakeGrabber { FailAtScale = 4 };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink());
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 4, Directory = TempDir(), FileStem = "t" });
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { 4, 2 }, g.Calls.Select(c => c.scale));
    }

    [Fact]
    public async Task Failure_returns_error_and_clears_busy_and_hide()
    {
        var vis = new FakeVisibility();
        var g = new FakeGrabber { FailAtScale = 1 };
        var s = new ScreenCaptureService(g, vis, new CaptureFileSink());
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t", HideDuringCapture = VisibilityLayers.GameHud });
        Assert.False(r.Success);
        Assert.False(s.IsCapturing);
        Assert.Equal(0, vis.Live);
    }

    [Fact]
    public async Task Invalid_request_never_grabs()
    {
        var g = new FakeGrabber();
        var r = await new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink())
            .CaptureAsync(new CaptureRequest { Scale = 3, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Empty(g.Calls);
    }
}
