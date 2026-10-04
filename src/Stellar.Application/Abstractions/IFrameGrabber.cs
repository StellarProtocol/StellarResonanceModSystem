using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Raw frame, rows bottom-up (Unity order). <see cref="Jpeg"/> is set when JPG was requested. <see cref="Note"/> is
/// a player-readable remark about how the frame was made (for example ReShade was not ready), or null.</summary>
internal sealed record FrameGrab(byte[] RgbaBottomUp, int Width, int Height, byte[]? Jpeg, string? Note = null);

/// <summary>
/// Draw ReShade's active effects into this capture. <see cref="Active"/> holds only enabled techniques (never empty
/// when passed). When <see cref="Shaped"/>, the ones that use depth are switched off for this capture only (D8) — the
/// depth buffer is the screen's, which does not line up with a differently shaped render. When
/// <see cref="SkipSizeLocked"/>, the size-locked ones (<see cref="ReShadeTechnique.SizeLocked"/>) are switched off too
/// (a capture that is not screen-sized and cannot fall back to it).
/// </summary>
internal sealed record ReShadeCaptureOptions(bool Shaped, IReadOnlyList<ReShadeTechnique> Active, bool SkipSizeLocked = false)
{
    /// <summary>The note a capture carries when ReShade did not draw in time and the photo was taken without it.</summary>
    internal const string NotReadyNote = Stellar.Application.Services.ReShadeCaptureNotes.NotReady;
}

internal sealed class FrameGrabException : Exception
{
    public FrameGrabException(string message) : base(message) { }
}

/// <summary>
/// What to render: the output size, and — for a shaped capture only — the camera's aspect (<see cref="Size"/>'s
/// width ÷ height) and vertical view angle (<see cref="CaptureSizing.VerticalFieldOfView"/>) for that one render,
/// restored right after it. A window-shaped grab (<see cref="Shaped"/> false) leaves the camera untouched.
/// <see cref="ReShade"/> non-null = draw ReShade's effects into the capture (see <see cref="ReShadeCaptureOptions"/>).
/// </summary>
internal readonly record struct GrabTarget(CaptureSize Size, bool Shaped, ReShadeCaptureOptions? ReShade = null);

/// <summary>
/// Grabs a rendered frame. Completes on the main thread (no RunContinuationsAsynchronously).
/// Extends <see cref="IMainThreadResume"/> — the resume mechanism it defines also backs
/// <c>IScreenCapture.IsCapturing</c>'s off-thread-to-main-thread handoff; a task that never
/// completes there wedges it the same way it would wedge any other caller.
/// </summary>
internal interface IFrameGrabber : IMainThreadResume
{
    (int Width, int Height) ScreenSize { get; }
    /// <summary>The GPU's largest texture side (Unity <c>SystemInfo.maxTextureSize</c>).</summary>
    int MaxTextureSize { get; }
    Task<FrameGrab> GrabAsync(GrabTarget target, int settleFrames, CaptureFormat format, int jpgQuality);
}
