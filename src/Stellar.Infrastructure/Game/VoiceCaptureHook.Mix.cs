using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Stellar.Infrastructure.Game;

// The Lock hook + the N-source mix. LockHook runs on GVoice's native capture thread every ~20 ms frame:
// no IL2CPP/game calls, no allocation, no logging, no locks — only volatile/Interlocked state, and nothing may throw
// out of it. (The very first reverse-P/Invoke on that foreign thread makes CoreCLR set up a managed Thread for it —
// a one-time cost, unavoidable and harmless.) Generalised from the plugin's single global clip to iterating the
// fixed VoiceMixer.Slots array: each playing slot whose clip has a converted buffer matching the current capture
// format is additively saturating-mixed into the SAME destination region (true mixing of all voices + the mic).
internal static unsafe partial class VoiceCaptureHook
{
    // Diagnostics.
    private static long s_lockCalls, s_lockMatched, s_lockMixed;
    private static int  s_lastHr;

    // Probe-mode per-buffer format cache: GetFormat only when a new buffer pointer shows up.
    private static IntPtr s_probeSelf;

    public static long LockCalls   => Interlocked.Read(ref s_lockCalls);
    public static long LockMatched => Interlocked.Read(ref s_lockMatched);
    public static long LockMixed   => Interlocked.Read(ref s_lockMixed);
    public static int  LastHr      => Volatile.Read(ref s_lastHr);

    // The two regions DirectSound's Lock returns (second is the ring-buffer wrap, often empty), deref'd once.
    private readonly struct LockRegions
    {
        public readonly IntPtr D1, D2;
        public readonly uint   N1, N2;
        public LockRegions(IntPtr d1, uint n1, IntPtr d2, uint n2) { D1 = d1; N1 = n1; D2 = d2; N2 = n2; }
    }

    // One slot's mix inputs, bundled so MixRegion stays under the 5-parameter gate.
    private readonly struct MixParams
    {
        public readonly short[] Pcm;
        public readonly int     Gain;   // Q8 (256 = unity)
        public readonly bool    Loop;
        public MixParams(short[] pcm, int gain, bool loop) { Pcm = pcm; Gain = gain; Loop = loop; }
    }

#pragma warning disable STELLAR0003 // Native DirectSound Lock ABI: the parameter list is fixed by the callee signature; no parameter object is possible.
    [UnmanagedCallersOnly]
    private static int LockHook(IntPtr self, uint off, uint bytes, IntPtr* p1, uint* n1, IntPtr* p2, uint* n2, uint flags)
    {
        int hr = ((delegate* unmanaged<IntPtr, uint, uint, IntPtr*, uint*, IntPtr*, uint*, uint, int>)s_origLock)
                 (self, off, bytes, p1, n1, p2, n2, flags);
        try
        {
            Interlocked.Increment(ref s_lockCalls);
            s_lastHr = hr;
            // flags != 0 = DSCBLOCK_ENTIREBUFFER (GVoice's pause path locks+memsets the whole ring) — never mix there.
            if (hr < 0 || bytes == 0 || flags != 0) return hr;

            if (ProbeMode)
            {
                if (self != s_probeSelf) { LearnFormat(self); s_probeSelf = self; }
            }
            else if (self != s_gvoiceBuf) return hr;
            Interlocked.Increment(ref s_lockMatched);

            if (!FormatSupported) return hr;
            var regions = new LockRegions(p1 != null ? *p1 : IntPtr.Zero, n1 != null ? *n1 : 0,
                                          p2 != null ? *p2 : IntPtr.Zero, n2 != null ? *n2 : 0);
            if (MixAllSlots(in regions, s_fmtRate, s_fmtCh)) Interlocked.Increment(ref s_lockMixed);
        }
        catch { }
        return hr;
    }
#pragma warning restore STELLAR0003

    // Mixes every playing slot whose clip is converted for the live format additively into the same regions.
    // Returns true if at least one slot was mixed this frame.
    private static bool MixAllSlots(in LockRegions r, int fmtRate, int fmtCh)
    {
        var slots = VoiceMixer.Slots;
        bool mixedAny = false;
        for (int i = 0; i < slots.Length; i++)
        {
            var slot = slots[i];
            if (slot.InUse == 0 || slot.Playing == 0) continue;
            var clip = slot.Clip;
            if (clip == null) continue;
            var conv = clip.Converted;
            // Mix only when the clip was converted for THIS capture format; a pending re-conversion skips frames.
            if (conv == null || conv.Rate != fmtRate || conv.Channels != fmtCh) continue;

            var mp = new MixParams(conv.Pcm, slot.GainQ8, slot.Loop != 0);
            int start = Volatile.Read(ref slot.Pos);
            int pos   = start;
            bool more = MixRegion(r.D1, r.N1, in mp, ref pos) && MixRegion(r.D2, r.N2, in mp, ref pos);
            // CAS so a Play/Stop/clip-swap that rewound slot.Pos during this frame wins over our stale advance.
            Interlocked.CompareExchange(ref slot.Pos, pos, start);
            if (!more) slot.Playing = 0;
            mixedAny = true;
        }
        return mixedAny;
    }

    // Additive saturating mix of 16-bit samples into the destination (summing with the mic + other voices already
    // there). Returns false when a non-looping clip ran out.
    private static bool MixRegion(IntPtr dst, uint nBytes, in MixParams mp, ref int pos)
    {
        if (dst == IntPtr.Zero || nBytes < 2) return true;
        int count = (int)(nBytes >> 1);
        int len   = mp.Pcm.Length;
        int gain  = mp.Gain;
        bool loop = mp.Loop;
        short* d  = (short*)dst;
        fixed (short* src = mp.Pcm)   // pinning, not allocation
        {
            for (int i = 0; i < count; i++)
            {
                if (pos >= len)
                {
                    if (!loop || len == 0) return false;
                    pos = 0;
                }
                int v = d[i] + ((src[pos++] * gain) >> 8);
                if (v > short.MaxValue) v = short.MaxValue;
                else if (v < short.MinValue) v = short.MinValue;
                d[i] = (short)v;
            }
        }
        return true;
    }

    // Probe mode only: IDirectSoundCaptureBuffer::GetFormat(+0x28) into a stack buffer big enough for
    // WAVEFORMATEXTENSIBLE (40 bytes) so an extensible format doesn't fail the call. Republishes only on change.
    private static void LearnFormat(IntPtr self)
    {
        byte* buf = stackalloc byte[64];
        uint written = 0;
        int hr = ((delegate* unmanaged<IntPtr, WaveFormatEx*, uint, uint*, int>)VoiceNative.Slot(self, SlotGetFormat))
                 (self, (WaveFormatEx*)buf, 64, &written);
        if (hr < 0 || written < 16) return;
        var f = (WaveFormatEx*)buf;
        if (FormatVersion > 0 && f->SamplesPerSec == (uint)s_fmtRate && f->Channels == s_fmtCh && f->BitsPerSample == s_fmtBits) return;
        PublishFormat(f);
    }

    public static string CounterLine =>
        $"Lock calls {LockCalls} · matched {LockMatched} · mixed {LockMixed} · last hr 0x{LastHr:X8}";
}
