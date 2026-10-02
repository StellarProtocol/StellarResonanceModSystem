using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>Captures the rendered screen to an image file. Call from the main thread.</summary>
/// <remarks>
/// PNG is compressed and streamed to the file on a thread-pool thread. JPG is encoded on the MAIN thread
/// (<c>ImageConversion.EncodeToJPG</c> on the captured texture), which costs one frame of encode time: the
/// thread-safe <c>EncodeArrayToJPG</c> would need a second full-frame IL2CPP pixel array, and it may only be called
/// from a thread attached to the IL2CPP runtime (<c>il2cpp_thread_attach</c>) — neither BepInEx nor Il2CppInterop
/// attaches managed pool threads, and the framework makes no IL2CPP call off the main thread anywhere.
/// </remarks>
public interface IScreenCapture
{
    /// <summary>
    /// Captures one frame; never throws — failures come back in the result.
    /// The returned task completes on the main thread.
    /// </summary>
    Task<CaptureResult> CaptureAsync(CaptureRequest request);
    /// <summary>True while a capture is in flight (a second request fails fast).</summary>
    bool IsCapturing { get; }
    /// <summary>
    /// The size <see cref="CaptureAsync"/> will write for <paramref name="request"/> on the current window — scale caps,
    /// the photo shape (<see cref="CaptureRequest.Aspect"/>) and the GPU's texture limit included — so a plugin can show
    /// the REAL output size before the shot. Empty when the scale or the shape is invalid. Only the out-of-memory retry
    /// at 2× (after a failed 4× grab) can still make the written image smaller. Call from the main thread.
    /// </summary>
    CaptureSize PlanSize(CaptureRequest request);
    /// <summary>
    /// The GPU's largest texture side in pixels (<c>SystemInfo.maxTextureSize</c>; <see cref="CaptureSizing.MaxLongSide"/>
    /// when it cannot be read) — the limit <see cref="PlanSize"/> and the capture apply. Pass it to
    /// <see cref="CaptureSizing.EffectiveScale"/> so a scale shown to the player agrees with <see cref="PlanSize"/>.
    /// </summary>
    int MaxTextureSize { get; }
}
