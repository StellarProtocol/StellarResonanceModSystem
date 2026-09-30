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

/// <summary>Grabs a rendered frame. Completes on the main thread (no RunContinuationsAsynchronously).</summary>
internal interface IFrameGrabber
{
    (int Width, int Height) ScreenSize { get; }
    Task<FrameGrab> GrabAsync(int scale, int settleFrames, CaptureFormat format, int jpgQuality);

    /// <summary>
    /// Resumes the caller on the main thread. Infrastructure implements this as a one-frame
    /// coroutine backed by a TCS created without RunContinuationsAsynchronously, so the
    /// continuation runs synchronously on the main thread when the coroutine completes it.
    /// The implementation MUST always complete the returned task (fault it when the coroutine
    /// host is gone) — a task that never completes wedges <c>IScreenCapture.IsCapturing</c>.
    /// The same holds for <see cref="GrabAsync"/>.
    /// </summary>
    Task ResumeOnMainThreadAsync();
}
