using System;
using System.Threading;

namespace Stellar.Infrastructure.Game;

// One voice in the uplink mixer. Pre-allocated once (VoiceMixer.Slots) so claiming / playing / mixing never
// allocates. All fields are touched from the native capture thread (the Lock hook) and the main thread
// (VoiceSource setters + the maintainer), so every access goes through volatile / Interlocked. `Pos` is a plain
// int (a field passed by ref to Interlocked cannot be `volatile`) read/written via Volatile/Interlocked.
internal sealed class MixSlot
{
    public volatile VoiceClip? Clip;   // the public clip; the hook reads Clip.Converted (one volatile ref read)
    public int Pos;                    // next interleaved SAMPLE index into Clip.Converted.Pcm
    public volatile int Playing;       // 1 while actively mixing
    public volatile int GainQ8;        // 256 = unity (100 %)
    public volatile int Loop;          // 1 = loop at clip end
    public volatile int InUse;         // 1 while claimed by a live VoiceSource
}

// The process-global fixed-capacity mixer: a shared array of slots mixed together into the one GVoice uplink.
// Claiming / releasing is lock-free (CAS on InUse). The Lock hook (VoiceCaptureHook.Mix.cs) iterates Slots;
// VoiceSource drives one slot; the maintainer tick (VoiceInjectionService) reads AnyPlaying and reconverts clips.
internal static class VoiceMixer
{
    // Fixed cap (design §8 "pick a sane cap (16)"): CreateSource returns null — and the service logs — when full.
    public const int Capacity = 16;

    public static readonly MixSlot[] Slots = BuildSlots();

    private static MixSlot[] BuildSlots()
    {
        var a = new MixSlot[Capacity];
        for (int i = 0; i < a.Length; i++) a[i] = new MixSlot { GainQ8 = 256 };
        return a;
    }

    /// <summary>Claims a free slot (CAS on InUse), reset to defaults. Null when the mixer is at capacity. Main thread.</summary>
    public static MixSlot? ClaimSlot()
    {
        foreach (var s in Slots)
        {
            if (Interlocked.CompareExchange(ref s.InUse, 1, 0) != 0) continue;
            s.Clip = null;
            s.Playing = 0;
            s.GainQ8 = 256;
            s.Loop = 0;
            Interlocked.Exchange(ref s.Pos, 0);
            return s;
        }
        return null;
    }

    /// <summary>Returns a slot to the pool. Clears playback state BEFORE releasing InUse so a racing claim sees it clean.</summary>
    public static void ReleaseSlot(MixSlot s)
    {
        s.Playing = 0;
        s.Clip = null;
        Interlocked.Exchange(ref s.Pos, 0);
        Volatile.Write(ref s.InUse, 0);
    }

    /// <summary>True while any live source is set to play — drives the uplink-DSP apply/restore. Any thread.</summary>
    public static bool AnyPlaying
    {
        get
        {
            foreach (var s in Slots)
                if (s.InUse != 0 && s.Playing != 0) return true;
            return false;
        }
    }

    /// <summary>Number of claimed slots (diagnostics).</summary>
    public static int ActiveCount
    {
        get
        {
            int n = 0;
            foreach (var s in Slots) if (s.InUse != 0) n++;
            return n;
        }
    }

    /// <summary>Stops every slot (framework teardown / hook restore). Does not free the slots.</summary>
    public static void StopAll()
    {
        foreach (var s in Slots) s.Playing = 0;
    }

    /// <summary>
    /// Publishes a freshly-converted buffer for <paramref name="clip"/> and rescales the position of every slot
    /// referencing it from the OLD converted layout to the new one, so a mid-playback capture-format change keeps its
    /// time position (generalises the single-clip ReplaceClipKeepTime). First conversion (<paramref name="old"/> null)
    /// needs no rescale — those slots are parked at Pos 0 because the hook never advanced them without a matching buffer.
    /// Runs off the maintainer's conversion task; the publish is a single volatile ref write the hook reads cleanly, and
    /// each Pos rescale is an atomic Exchange — a one-frame glitch on the rare format change is acceptable.
    /// </summary>
    public static void ReplaceConverted(VoiceClip clip, ConvertedPcm? old, ConvertedPcm neu)
    {
        clip.Converted = neu;
        if (old == null || old.Rate <= 0 || old.Channels <= 0) return;
        foreach (var s in Slots)
        {
            if (s.InUse == 0 || s.Clip != clip) continue;
            int p = Volatile.Read(ref s.Pos);
            long frame = (long)p / old.Channels * neu.Rate / old.Rate;
            int np = (int)Math.Min(frame * neu.Channels, neu.Pcm.Length);
            Interlocked.Exchange(ref s.Pos, np);
        }
    }
}
