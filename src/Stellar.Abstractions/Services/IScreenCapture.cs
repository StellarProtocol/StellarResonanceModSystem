using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>Captures the rendered screen to an image file. Call from the main thread.</summary>
public interface IScreenCapture
{
    /// <summary>Captures one frame; never throws — failures come back in the result.</summary>
    Task<CaptureResult> CaptureAsync(CaptureRequest request);
    /// <summary>True while a capture is in flight (a second request fails fast).</summary>
    bool IsCapturing { get; }
}
