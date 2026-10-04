using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Imaging;
namespace Stellar.Application.Services;

// ReShade options + the size-locked guard live in ScreenCaptureService.ReShade.cs.
internal sealed partial class ScreenCaptureService : IScreenCapture
{
    private const int SettleFramesWhenHiding = 2;
    private const int SettleFramesForScaleGuard = 1;   // a render-scale write reallocates the pipeline's targets
    private readonly IFrameGrabber _grabber;
    private readonly ISceneVisibility _visibility;
    private readonly CaptureFileSink _sink;
    private readonly Action<string> _log;
    private readonly Func<IDisposable?>? _renderScaleGuard;
    private readonly IReShade? _reShade;

    /// <param name="renderScaleGuard">Spec 2026-10-01 § 4: when set, held around the grab frame to drop a
    /// supersampled render scale (the N× grab is supersampled already). Null = off (the default until the in-game
    /// measurement shows the off-screen render is multiplied by the render scale).</param>
    /// <param name="reShade">When set, a request with <see cref="CaptureRequest.ApplyReShade"/> has ReShade's active
    /// effects drawn into the capture — only while ReShade is available and its effects are on. Null = never.</param>
    public ScreenCaptureService(IFrameGrabber grabber, ISceneVisibility visibility, CaptureFileSink sink, Action<string> log,
        Func<IDisposable?>? renderScaleGuard = null, IReShade? reShade = null)
    {
        _grabber = grabber;
        _visibility = visibility;
        _sink = sink;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _renderScaleGuard = renderScaleGuard;
        _reShade = reShade;
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
            var (grabbed, guardNote) = await GrabWithFallback(request, scale, SettleFrames(hide, scaleGuard));
            FrameGrab? grab = grabbed;
            grabbed = null!;
            scaleGuard?.Dispose(); // still on the main thread (grabber contract)
            scaleGuard = null;
            hide?.Dispose();
            hide = null;
            var (width, height, note) = (grab.Width, grab.Height, grab.Note);
            // Off-thread: stream the PNG straight into the file (or write the JPG bytes). The frame reference is
            // dropped the moment the write returns, so the pixel buffer is collectable before the main-thread resume.
            var path = await Task.Run(() =>
            {
                var g = grab!;
                grab = null;
                return Save(g, request);
            });
            await ResumeQuietly(); // never throws, so the catch below can never resume a second time
            var ok = CaptureResult.Ok(path, width, height);
            return guardNote is null && note is null ? ok : ok with { Notes = Notes(guardNote, note) };
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
    // and caps both sides equally inside CaptureSizing.OutputSize. The ReShade size guard may lower a window-shaped
    // scale to 1 (its note comes back alongside the grab).
    private async Task<(FrameGrab Grab, string? GuardNote)> GrabWithFallback(CaptureRequest r, int effectiveScale, int settle)
    {
        var (reShade, scale, guardNote) = GuardSize(r, ReShadeOptions(r), r.Aspect is null ? effectiveScale : r.Scale);
        try
        {
            return (await _grabber.GrabAsync(Target(r, scale, reShade), settle, r.Format, r.JpgQuality), guardNote);
        }
        // A 4× frame can also run the managed heap out (OutOfMemoryException) — 2× gets the same second chance.
        catch (Exception ex) when (scale > 2 && ex is FrameGrabException or OutOfMemoryException)
        {
            return (await _grabber.GrabAsync(Target(r, 2, reShade), settle, r.Format, r.JpgQuality), guardNote);
        }
    }

    private static IReadOnlyList<string> Notes(string? first, string? second) =>
        first is null ? new[] { second! } : second is null ? new[] { first } : new[] { first, second };

    private GrabTarget Target(CaptureRequest r, int scale, ReShadeCaptureOptions? reShade)
    {
        var (w, h) = _grabber.ScreenSize;
        return r.Aspect is null
            ? new GrabTarget(new CaptureSize(w * scale, h * scale), Shaped: false, reShade)
            : new GrabTarget(CaptureSizing.OutputSize(w, h, scale, r.Aspect, _grabber.MaxTextureSize), Shaped: true, reShade);
    }

    private static string MapError(Exception ex) => ex switch
    {
        FrameGrabException => "The screen could not be captured.",
        IOException or UnauthorizedAccessException => "The screenshot could not be saved to that folder.",
        _ => "The screenshot failed unexpectedly.",
    };
}
