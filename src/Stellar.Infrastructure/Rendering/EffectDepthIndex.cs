using Stellar.Application.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Per effect file, whether it reads the depth buffer (<see cref="EffectDepthScanner"/>). D8 safety: unknown
/// counts as using depth, so a shaped photo switches it off rather than drawing it with a wrong depth buffer.</summary>
internal sealed class EffectDepthIndex : EffectScanIndex
{
    internal EffectDepthIndex(IEffectFileSystem fs) : base(fs, EffectDepthScanner.UsesDepth) { }
}
