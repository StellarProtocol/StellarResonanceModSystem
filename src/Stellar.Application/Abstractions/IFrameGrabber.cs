using System;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Raw frame, rows bottom-up (Unity order). <see cref="Jpeg"/> is set when JPG was requested.</summary>
internal sealed record FrameGrab(byte[] RgbaBottomUp, int Width, int Height, byte[]? Jpeg);

internal sealed class FrameGrabException : Exception
{
    public FrameGrabException(string message) : base(message) { }
}

/// <summary>
/// What to render: the output size, and — for a shaped capture only — the camera's aspect (<see cref="Size"/>'s
/// width ÷ height) and vertical view angle (<see cref="CaptureSizing.VerticalFieldOfView"/>) for that one render,
/// restored right after it. A window-shaped grab (<see cref="Shaped"/> false) leaves the camera untouched.
/// </summary>
internal readonly record struct GrabTarget(CaptureSize Size, bool Shaped);

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
