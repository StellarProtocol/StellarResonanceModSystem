using System;
using System.IO;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
namespace Stellar.Application.Services;

internal sealed class ScreenCaptureService : IScreenCapture
{
    private const int SettleFramesWhenHiding = 2;
    private readonly IFrameGrabber _grabber;
    private readonly ISceneVisibility _visibility;
    private readonly CaptureFileSink _sink;
    private readonly Action<string> _log;

    public ScreenCaptureService(IFrameGrabber grabber, ISceneVisibility visibility, CaptureFileSink sink, Action<string> log)
    {
        _grabber = grabber;
        _visibility = visibility;
        _sink = sink;
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsCapturing { get; private set; }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request)
    {
        if (IsCapturing) return CaptureResult.Fail("A screenshot is already being taken.");
        IDisposable? hide = null;
        try
        {
            IsCapturing = true;
            var (w, h) = _grabber.ScreenSize;
            var (scale, error) = CaptureRequestValidator.Validate(request, w, h);
            if (error is not null) return CaptureResult.Fail(error);
            hide = request.HideDuringCapture == VisibilityLayers.None ? null : _visibility.Hide(request.HideDuringCapture);
            FrameGrab? grab = await GrabWithFallback(request, scale, hide is null ? 0 : SettleFramesWhenHiding);
            hide?.Dispose(); // still on the main thread (grabber contract)
            hide = null;
            var (width, height) = (grab.Width, grab.Height);
            // Off-thread: stream the PNG straight into the file (or write the JPG bytes). The frame reference is
            // dropped the moment the write returns, so the pixel buffer is collectable before the main-thread resume.
            var path = await Task.Run(() =>
            {
                var g = grab!;
                grab = null;
                return Save(g, request);
            });
            await ResumeQuietly(); // never throws, so the catch below can never resume a second time
            return CaptureResult.Ok(path, width, height);
        }
        catch (Exception ex)
        {
            await ResumeQuietly();
            _log(ex.ToString());
            return CaptureResult.Fail(MapError(ex));
        }
        finally
        {
            hide?.Dispose();
            IsCapturing = false;
        }
    }

    private string Save(FrameGrab g, CaptureRequest r)
    {
        if (g.Jpeg is { } jpeg) return _sink.Write(r.Directory, r.FileStem, ".jpg", jpeg);
        PngEncoder.EnsureEncodable(g);   // a bad frame fails before a file is created
        return _sink.WriteNew(r.Directory, r.FileStem, ".png", s => PngEncoder.Encode(g, s));
    }

    // A faulting/throwing resume must not escape CaptureAsync: the file (if any) is already written and the
    // caller is owed a result. The grabber contract says the resume always completes; this is the backstop.
    private async Task ResumeQuietly()
    {
        try { await _grabber.ResumeOnMainThreadAsync(); }
        catch (Exception ex) { _log("Capture could not resume on the main thread: " + ex); }
    }

    private async Task<FrameGrab> GrabWithFallback(CaptureRequest r, int scale, int settle)
    {
        try
        {
            return await _grabber.GrabAsync(scale, settle, r.Format, r.JpgQuality);
        }
        // A 4× frame can also run the managed heap out (OutOfMemoryException) — 2× gets the same second chance.
        catch (Exception ex) when (scale > 2 && ex is FrameGrabException or OutOfMemoryException)
        {
            return await _grabber.GrabAsync(2, settle, r.Format, r.JpgQuality);
        }
    }

    private static string MapError(Exception ex) => ex switch
    {
        FrameGrabException => "The screen could not be captured.",
        IOException or UnauthorizedAccessException => "The screenshot could not be saved to that folder.",
        _ => "The screenshot failed unexpectedly.",
    };
}
