using System;
using System.Threading;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Framework implementation of <see cref="IVoiceSource"/> — a handle onto one claimed <see cref="MixSlot"/> in the
/// process-global mixer. Property setters write the slot's volatile fields; the native Lock hook reads them. Not
/// thread-safe for concurrent caller mutation (the design contract). <see cref="Dispose"/> frees the mixer slot and
/// is idempotent, so the per-plugin façade can dispose it again on plugin teardown after the plugin already did.
/// </summary>
internal sealed class VoiceSource : IVoiceSource
{
    private readonly MixSlot _slot;
    private IVoiceClip? _clip;
    private int _disposed;

    public VoiceSource(MixSlot slot) => _slot = slot;

    public IVoiceClip? Clip
    {
        get => _clip;
        set
        {
            _clip = value;
            // Assigning a new clip rewinds + stops: the old position is meaningless against different PCM.
            _slot.Playing = 0;
            _slot.Clip = value as VoiceClip;   // a foreign IVoiceClip is treated as "no playable clip"
            Interlocked.Exchange(ref _slot.Pos, 0);
        }
    }

    public float Volume
    {
        get => _slot.GainQ8 / 256f;
        set => _slot.GainQ8 = (int)Math.Round(Math.Clamp(value, 0f, 2f) * 256f);
    }

    public bool Loop
    {
        get => _slot.Loop != 0;
        set => _slot.Loop = value ? 1 : 0;
    }

    public bool Playing => _slot.Playing != 0;

    public TimeSpan Position
    {
        get
        {
            var conv = _slot.Clip?.Converted;
            if (conv == null || conv.Rate <= 0 || conv.Channels <= 0) return TimeSpan.Zero;
            long frame = (long)Volatile.Read(ref _slot.Pos) / conv.Channels;
            return TimeSpan.FromSeconds((double)frame / conv.Rate);
        }
    }

    public void Play()
    {
        if (_slot.Clip == null) return;          // nothing to play; stays idle
        Interlocked.Exchange(ref _slot.Pos, 0);
        _slot.Playing = 1;
    }

    public void Stop() => _slot.Playing = 0;     // position retained; Play() rewinds

    public void PlayOnce(IVoiceClip clip)
    {
        Clip = clip;
        Loop = false;
        Play();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _clip = null;
        VoiceMixer.ReleaseSlot(_slot);           // Playing=0, Clip=null, InUse=0 — frees the slot for reuse
    }
}
