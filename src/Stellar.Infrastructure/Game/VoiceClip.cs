using System;
using System.Threading;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Framework implementation of <see cref="IVoiceClip"/>. Holds the immutable SOURCE PCM (file / caller rate) plus a
/// lazily-built <see cref="ConvertedPcm"/> at the current capture format and the format version it was converted for.
/// The maintainer tick (<see cref="VoiceInjectionService"/>) (re)builds the converted buffer off-thread whenever a live
/// source references this clip at a format version it has not yet been converted for; the Lock hook only ever reads the
/// finished <see cref="Converted"/> buffer (one volatile reference read).
/// </summary>
internal sealed class VoiceClip : IVoiceClip
{
    public readonly DecodedAudio Source;

    // Published whole by the off-thread converter; the native Lock hook reads this reference once per frame (volatile).
    public volatile ConvertedPcm? Converted;

    private int _convertedVersion;  // capture-format version Converted was built for (0 = never)
    private int _converting;        // 1 while an off-thread conversion for this clip is in flight

    public VoiceClip(DecodedAudio source) => Source = source;

    public TimeSpan Duration => TimeSpan.FromSeconds(Source.Seconds);

    public int ConvertedVersion => Volatile.Read(ref _convertedVersion);

    /// <summary>Claims the single-conversion slot for this clip (CAS). False if a conversion is already running.</summary>
    public bool BeginConvert() => Interlocked.CompareExchange(ref _converting, 1, 0) == 0;

    /// <summary>Marks a finished conversion: records the format version and releases the conversion slot.</summary>
    public void EndConvert(int version)
    {
        Volatile.Write(ref _convertedVersion, version);
        Volatile.Write(ref _converting, 0);
    }

    /// <summary>Releases the conversion slot WITHOUT recording a version (a failed conversion retries next tick).</summary>
    public void AbortConvert() => Volatile.Write(ref _converting, 0);
}
