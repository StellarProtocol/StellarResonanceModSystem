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
            var grab = await GrabWithFallback(request, scale, hide is null ? 0 : SettleFramesWhenHiding);
            hide?.Dispose(); // still on the main thread (grabber contract)
            hide = null;
            var ext = request.Format == CaptureFormat.Jpg ? ".jpg" : ".png";
            var bytes = grab.Jpeg ?? await Task.Run(() => PngEncoder.Encode(grab));
            var path = await Task.Run(() => _sink.Write(request.Directory, request.FileStem, ext, bytes));
            await ResumeQuietly(); // never throws, so the catch below can never resume a second time
            return CaptureResult.Ok(path, grab.Width, grab.Height);
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
        catch (FrameGrabException) when (scale > 2)
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
