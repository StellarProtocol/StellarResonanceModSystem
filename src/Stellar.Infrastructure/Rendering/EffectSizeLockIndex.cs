using Stellar.Application.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Per effect file, whether it is size-locked — declares a texture sized from the screen
/// (<see cref="EffectSizeLockScanner"/>), which ReShade shares across sizes so a larger capture reads it wrong. Unknown
/// counts as locked, so the photo stays at screen size rather than coming out magnified.</summary>
internal sealed class EffectSizeLockIndex : EffectScanIndex
{
    internal EffectSizeLockIndex(IEffectFileSystem fs) : base(fs, EffectSizeLockScanner.IsSizeLocked) { }
}
