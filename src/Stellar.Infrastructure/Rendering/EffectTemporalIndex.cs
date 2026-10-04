using Stellar.Application.Services;

namespace Stellar.Infrastructure.Rendering;

/// <summary>Per effect file, whether it is temporal — its output depends on earlier frames
/// (<see cref="EffectTemporalScanner"/>), so an isolated photo warms it up first. Unknown counts as temporal (a warm-up
/// costs time; a missed one costs a wrong photo).</summary>
internal sealed class EffectTemporalIndex : EffectScanIndex
{
    internal EffectTemporalIndex(IEffectFileSystem fs) : base(fs, EffectTemporalScanner.IsTemporal) { }
}
