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
    private const int SettleFramesForScaleGuard = 1;   // a render-scale write reallocates the pipeline's targets
    private readonly IFrameGrabber _grabber;
    private readonly ISceneVisibility _visibility;
    private readonly CaptureFileSink _sink;
    private readonly Action<string> _log;
    private readonly Func<IDisposable?>? _renderScaleGuard;

    /// <param name="renderScaleGuard">Spec 2026-10-01 § 4: when set, held around the grab frame to drop a
    /// supersampled render scale (the N× grab is supersampled already). Null = off (the default until the in-game
    /// measurement shows the off-screen render is multiplied by the render scale).</param>
    public ScreenCaptureService(IFrameGrabber grabber, ISceneVisibility visibility, CaptureFileSink sink, Action<string> log,
        Func<IDisposable?>? renderScaleGuard = null)
    {
        _grabber = grabber;
        _visibility = visibility;
        _sink = sink;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _renderScaleGuard = renderScaleGuard;
    }

    public bool IsCapturing { get; private set; }

    public int MaxTextureSize => _grabber.MaxTextureSize;

    public CaptureSize PlanSize(CaptureRequest request)
    {
        if (CaptureRequestValidator.ShapeError(request) is not null) return default;
        var (w, h) = _grabber.ScreenSize;
        return CaptureSizing.OutputSize(w, h, request.Scale, request.Aspect, _grabber.MaxTextureSize);
    }

    public async Task<CaptureResult> CaptureAsync(CaptureRequest request)
    {
        if (IsCapturing) return CaptureResult.Fail("A screenshot is already being taken.");
        IDisposable? hide = null;
        IDisposable? scaleGuard = null;
        try
        {
            IsCapturing = true;
            var (w, h) = _grabber.ScreenSize;
            var (scale, error) = CaptureRequestValidator.Validate(request, w, h, _grabber.MaxTextureSize);
            if (error is not null) return CaptureResult.Fail(error);
            hide = request.HideDuringCapture == VisibilityLayers.None ? null : _visibility.Hide(request.HideDuringCapture);
            scaleGuard = _renderScaleGuard?.Invoke();
            FrameGrab? grab = await GrabWithFallback(request, scale, SettleFrames(hide, scaleGuard));
            scaleGuard?.Dispose(); // still on the main thread (grabber contract)
            scaleGuard = null;
            hide?.Dispose();
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
            scaleGuard?.Dispose();
            hide?.Dispose();
            IsCapturing = false;
        }
    }

    private static int SettleFrames(IDisposable? hide, IDisposable? scaleGuard) =>
        Math.Max(hide is null ? 0 : SettleFramesWhenHiding, scaleGuard is null ? 0 : SettleFramesForScaleGuard);

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

    // A window-shaped grab uses the validator's (already capped) scale; a shaped one sizes from the REQUESTED scale
    // and caps both sides equally inside CaptureSizing.OutputSize.
    private async Task<FrameGrab> GrabWithFallback(CaptureRequest r, int effectiveScale, int settle)
    {
        var scale = r.Aspect is null ? effectiveScale : r.Scale;
        try
        {
            return await _grabber.GrabAsync(Target(r, scale), settle, r.Format, r.JpgQuality);
        }
        // A 4× frame can also run the managed heap out (OutOfMemoryException) — 2× gets the same second chance.
        catch (Exception ex) when (scale > 2 && ex is FrameGrabException or OutOfMemoryException)
        {
            return await _grabber.GrabAsync(Target(r, 2), settle, r.Format, r.JpgQuality);
        }
    }

    private GrabTarget Target(CaptureRequest r, int scale)
    {
        var (w, h) = _grabber.ScreenSize;
        return r.Aspect is null
            ? new GrabTarget(new CaptureSize(w * scale, h * scale), Shaped: false)
            : new GrabTarget(CaptureSizing.OutputSize(w, h, scale, r.Aspect, _grabber.MaxTextureSize), Shaped: true);
    }

    private static string MapError(Exception ex) => ex switch
    {
        FrameGrabException => "The screen could not be captured.",
        IOException or UnauthorizedAccessException => "The screenshot could not be saved to that folder.",
        _ => "The screenshot failed unexpectedly.",
    };
}
