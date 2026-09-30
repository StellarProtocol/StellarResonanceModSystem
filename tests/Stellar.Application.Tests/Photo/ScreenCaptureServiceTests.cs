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
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class ScreenCaptureServiceTests : IDisposable
{
    private sealed class FakeGrabber : IFrameGrabber
    {
        public (int Width, int Height) ScreenSize { get; set; } = (4, 2);
        public int FailAtScale = -1;
        public bool FailWithOutOfMemory;
        public readonly List<(int scale, int settle)> Calls = new();
        public Func<bool>? HiddenDuringGrab;
        public bool SawHidden;
        public byte[]? JpegBytes;
        public bool ShortBuffer;
        public TaskCompletionSource<FrameGrab>? Pending;
        public int ResumeCallCount;
        public Action? OnResume;
        public bool ResumeThrows;
        public bool ResumeFaults;

        public Task<FrameGrab> GrabAsync(int scale, int settleFrames, CaptureFormat format, int jpgQuality)
        {
            Calls.Add((scale, settleFrames));
            SawHidden = HiddenDuringGrab?.Invoke() ?? false;
            if (scale == FailAtScale) throw FailWithOutOfMemory ? new OutOfMemoryException() : new FrameGrabException("oom");
            if (Pending is not null) return Pending.Task;
            var w = ScreenSize.Width * scale; var h = ScreenSize.Height * scale;
            var rgba = new byte[ShortBuffer ? 1 : w * h * 4];
            var jpeg = format == CaptureFormat.Jpg ? JpegBytes : null;
            return Task.FromResult(new FrameGrab(rgba, w, h, jpeg));
        }

        public Task ResumeOnMainThreadAsync()
        {
            ResumeCallCount++;
            OnResume?.Invoke();
            if (ResumeThrows) throw new InvalidOperationException("host gone");
            if (ResumeFaults) return Task.FromException(new FrameGrabException("host gone"));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVisibility : ISceneVisibility
    {
        public int Live;
        public IDisposable Hide(VisibilityLayers layers) { Live++; return new D(() => Live--); }
        public VisibilityLayers Hidden => Live > 0 ? VisibilityLayers.GameHud : VisibilityLayers.None;
        public VisibilityLayers Available => VisibilityLayers.GameHud;
        public event Action<VisibilityLayers>? Changed { add { } remove { } }
        private sealed class D : IDisposable { private Action? _a; public D(Action a) => _a = a; public void Dispose() { _a?.Invoke(); _a = null; } }
    }

    /// <summary>Throws once (the next Hide call), then behaves like a normal, working visibility.</summary>
    private sealed class ThrowOnceVisibility : ISceneVisibility
    {
        public bool ThrowNext = true;
        public IDisposable Hide(VisibilityLayers layers)
        {
            if (ThrowNext) { ThrowNext = false; throw new InvalidOperationException("boom"); }
            return new NullToken();
        }
        public VisibilityLayers Hidden => VisibilityLayers.None;
        public VisibilityLayers Available => VisibilityLayers.GameHud;
        public event Action<VisibilityLayers>? Changed { add { } remove { } }
        private sealed class NullToken : IDisposable { public void Dispose() { } }
    }

    private static void NoLog(string _) { }

    private readonly List<string> _tempDirs = new();

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "photo-svc-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task Hides_during_grab_and_restores_after()
    {
        var vis = new FakeVisibility();
        var g = new FakeGrabber();
        g.HiddenDuringGrab = () => vis.Live > 0;
        var s = new ScreenCaptureService(g, vis, new CaptureFileSink(), NoLog);
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
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 4, Directory = TempDir(), FileStem = "t" });
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { 4, 2 }, g.Calls.Select(c => c.scale));
    }

    // Photo Studio fw fix round (perf review): a 4× grab that runs the managed heap out must also fall back to 2×.
    [Fact]
    public async Task Four_x_out_of_memory_retries_once_at_two_x()
    {
        var g = new FakeGrabber { FailAtScale = 4, FailWithOutOfMemory = true };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 4, Directory = TempDir(), FileStem = "t" });
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { 4, 2 }, g.Calls.Select(c => c.scale));
    }

    [Fact]
    public async Task Png_is_streamed_to_a_decodable_file_and_a_failed_encode_leaves_no_file()
    {
        var dir = TempDir();
        var ok = await new ScreenCaptureService(new FakeGrabber(), new FakeVisibility(), new CaptureFileSink(), NoLog)
            .CaptureAsync(new CaptureRequest { Scale = 1, Directory = dir, FileStem = "t" });
        Assert.True(ok.Success, ok.Error);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, File.ReadAllBytes(ok.Path!)[..8]);

        var bad = await new ScreenCaptureService(new FakeGrabber { ShortBuffer = true }, new FakeVisibility(), new CaptureFileSink(), NoLog)
            .CaptureAsync(new CaptureRequest { Scale = 1, Directory = dir, FileStem = "u" });
        Assert.False(bad.Success);
        Assert.Equal(new[] { ok.Path }, Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Two_x_failure_is_not_retried()
    {
        var g = new FakeGrabber { FailAtScale = 2 };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 2, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Equal(new[] { 2 }, g.Calls.Select(c => c.scale));
    }

    [Fact]
    public async Task Failure_returns_error_and_clears_busy_and_hide()
    {
        var vis = new FakeVisibility();
        var g = new FakeGrabber { FailAtScale = 1 };
        var s = new ScreenCaptureService(g, vis, new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t", HideDuringCapture = VisibilityLayers.GameHud });
        Assert.False(r.Success);
        Assert.False(s.IsCapturing);
        Assert.Equal(0, vis.Live);
    }

    [Fact]
    public async Task Invalid_request_never_grabs()
    {
        var g = new FakeGrabber();
        var r = await new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog)
            .CaptureAsync(new CaptureRequest { Scale = 3, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Empty(g.Calls);
    }

    [Fact]
    public async Task Already_capturing_fails_fast_without_waiting_for_the_first_capture()
    {
        var pending = new TaskCompletionSource<FrameGrab>();
        var g = new FakeGrabber { Pending = pending };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var first = s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t1" });
        Assert.True(s.IsCapturing);

        var second = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t2" });
        Assert.False(second.Success);
        Assert.Equal("A screenshot is already being taken.", second.Error);

        // Let the first capture complete so it doesn't leak into another test.
        pending.SetResult(new FrameGrab(new byte[4 * 2 * 4], 4, 2, null));
        var completedFirst = await first;
        Assert.True(completedFirst.Success, completedFirst.Error);
    }

    [Fact]
    public async Task Jpg_format_uses_grab_jpeg_bytes_and_writes_jpg_extension()
    {
        var g = new FakeGrabber { JpegBytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 } };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Format = CaptureFormat.Jpg, Directory = TempDir(), FileStem = "t" });
        Assert.True(r.Success, r.Error);
        Assert.EndsWith(".jpg", r.Path);
        Assert.Equal(g.JpegBytes, await File.ReadAllBytesAsync(r.Path!));
    }

    [Fact]
    public async Task Resumes_on_main_thread_before_clearing_busy_on_success()
    {
        var g = new FakeGrabber();
        ScreenCaptureService? svc = null;
        bool? capturingDuringResume = null;
        g.OnResume = () => capturingDuringResume = svc!.IsCapturing;
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        svc = s;

        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });

        Assert.True(r.Success, r.Error);
        Assert.Equal(1, g.ResumeCallCount);
        Assert.True(capturingDuringResume, "IsCapturing must still be true while ResumeOnMainThreadAsync runs.");
        Assert.False(s.IsCapturing);
    }

    [Fact]
    public async Task Resumes_on_main_thread_before_clearing_busy_on_encode_failure()
    {
        var g = new FakeGrabber { ShortBuffer = true }; // triggers an exception inside the off-thread PNG encode
        ScreenCaptureService? svc = null;
        bool? capturingDuringResume = null;
        g.OnResume = () => capturingDuringResume = svc!.IsCapturing;
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        svc = s;

        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });

        Assert.False(r.Success);
        Assert.Equal(1, g.ResumeCallCount);
        Assert.True(capturingDuringResume, "IsCapturing must still be true while ResumeOnMainThreadAsync runs.");
        Assert.False(s.IsCapturing);
    }

    [Fact]
    public async Task Hide_throwing_fails_cleanly_and_a_later_capture_still_works()
    {
        var vis = new ThrowOnceVisibility();
        var g = new FakeGrabber();
        var s = new ScreenCaptureService(g, vis, new CaptureFileSink(), NoLog);

        var r1 = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t1", HideDuringCapture = VisibilityLayers.GameHud });
        Assert.False(r1.Success);
        Assert.False(s.IsCapturing);

        var r2 = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t2", HideDuringCapture = VisibilityLayers.GameHud });
        Assert.True(r2.Success, r2.Error);
        Assert.False(s.IsCapturing);
    }

    [Fact]
    public async Task Grab_failure_maps_to_a_player_readable_message()
    {
        // Scale 1 has no fallback, so the FrameGrabException surfaces directly as a grab failure.
        var g = new FakeGrabber { FailAtScale = 1 };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Equal("The screen could not be captured.", r.Error);
    }

    [Fact]
    public async Task Unexpected_failure_maps_to_a_generic_player_readable_message()
    {
        var g = new FakeGrabber { ShortBuffer = true };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Equal("The screenshot failed unexpectedly.", r.Error);
    }

    [Fact]
    public async Task Failure_never_leaks_raw_exception_text_but_logs_it()
    {
        var g = new FakeGrabber { FailAtScale = 1 };
        var logged = new List<string>();
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), logged.Add);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.DoesNotContain("FrameGrabException", r.Error);
        Assert.Single(logged);
        Assert.Contains("FrameGrabException", logged[0]);
    }

    // Tasks 1-5 review carry-over (a): a faulting/throwing ResumeOnMainThreadAsync must not escape CaptureAsync,
    // and the success path must not resume a second time from the catch.
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Resume_failure_after_a_written_file_still_reports_success_and_resumes_once(bool throws)
    {
        var g = new FakeGrabber { ResumeThrows = throws, ResumeFaults = !throws };
        var logged = new List<string>();
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), logged.Add);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });
        Assert.True(r.Success, r.Error);
        Assert.True(File.Exists(r.Path));
        Assert.Equal(1, g.ResumeCallCount);
        Assert.False(s.IsCapturing);
        Assert.Single(logged);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Resume_failure_on_the_error_path_does_not_escape(bool throws)
    {
        var g = new FakeGrabber { ShortBuffer = true, ResumeThrows = throws, ResumeFaults = !throws };
        var s = new ScreenCaptureService(g, new FakeVisibility(), new CaptureFileSink(), NoLog);
        var r = await s.CaptureAsync(new CaptureRequest { Scale = 1, Directory = TempDir(), FileStem = "t" });
        Assert.False(r.Success);
        Assert.Equal("The screenshot failed unexpectedly.", r.Error);
        Assert.Equal(1, g.ResumeCallCount);
        Assert.False(s.IsCapturing);
    }

    [Fact]
    public void Log_sink_is_required() =>
        Assert.Throws<ArgumentNullException>(() => new ScreenCaptureService(new FakeGrabber(), new FakeVisibility(), new CaptureFileSink(), null!));
}
