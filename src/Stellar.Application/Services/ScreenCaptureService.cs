using System;
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

    public ScreenCaptureService(IFrameGrabber grabber, ISceneVisibility visibility, CaptureFileSink sink)
    {
        _grabber = grabber;
        _visibility = visibility;
        _sink = sink;
    }

    public bool IsCapturing { get; private set; }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request)
    {
        if (IsCapturing) return CaptureResult.Fail("A screenshot is already being taken.");
        var (w, h) = _grabber.ScreenSize;
        var (scale, error) = CaptureRequestValidator.Validate(request, w, h);
        if (error is not null) return CaptureResult.Fail(error);
        IsCapturing = true;
        var hide = request.HideDuringCapture == VisibilityLayers.None ? null : _visibility.Hide(request.HideDuringCapture);
        try
        {
            var grab = await GrabWithFallback(request, scale, hide is null ? 0 : SettleFramesWhenHiding);
            hide?.Dispose(); // still on the main thread (grabber contract)
            hide = null;
            var ext = request.Format == CaptureFormat.Jpg ? ".jpg" : ".png";
            var bytes = grab.Jpeg ?? await Task.Run(() => PngEncoder.Encode(grab));
            var path = await Task.Run(() => _sink.Write(request.Directory, request.FileStem, ext, bytes));
            return CaptureResult.Ok(path, grab.Width, grab.Height);
        }
        catch (Exception ex)
        {
            return CaptureResult.Fail(ex.Message);
        }
        finally
        {
            hide?.Dispose();
            IsCapturing = false;
        }
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
}
