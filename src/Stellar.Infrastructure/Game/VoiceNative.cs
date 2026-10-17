using System;
using System.Runtime.InteropServices;

namespace Stellar.Infrastructure.Game;

// Raw Win32 / COM surface for the voice-injection capture hook + Media Foundation decode (promoted from
// StellarExperimentPlugin, in-game validated 2026-10-05; spec: Voice-Chat-GVoice.md). Every signature is
// BLITTABLE (pointers / ints only, no SetLastError, no string marshalling except the one
// LoadLibraryW/GetModuleHandleW path string, which is only called from managed init code). That matters
// because VirtualProtect is called from inside the CreateCaptureBuffer hook on a native thread — a blittable
// DllImport has no marshalling stub allocations.
internal static unsafe class VoiceNative
{
    // ── kernel32 ──────────────────────────────────────────────────────────────────────────────────────────────────
    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string name);

    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", ExactSpelling = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern IntPtr GetProcAddress(IntPtr module, string name);

    [DllImport("kernel32", ExactSpelling = true)]
    public static extern int VirtualProtect(IntPtr addr, UIntPtr size, uint newProtect, uint* oldProtect);

    // PAGE_EXECUTE_READWRITE, not PAGE_READWRITE: the IAT/vtable page may share protection with code on some
    // layouts — dropping EXECUTE for the write window would fault any other thread running code on that page.
    private const uint PageExecuteReadWrite = 0x40;

    // 8-byte aligned pointer write through a temporary protection flip. Aligned 8-byte stores are atomic on x64, so
    // a concurrent reader of the slot sees either the old or the new pointer, never a torn one.
    public static bool WritePointer(IntPtr slot, IntPtr value)
    {
        uint old;
        if (VirtualProtect(slot, (UIntPtr)8, PageExecuteReadWrite, &old) == 0) return false;
        *(IntPtr*)slot = value;
        uint ignored;
        VirtualProtect(slot, (UIntPtr)8, old, &ignored);
        return true;
    }

    // ── ole32 (COM apartment for the Media Foundation decode thread) ──────────────────────────────────────────────
    [DllImport("ole32", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32", ExactSpelling = true)]
    public static extern void CoUninitialize();

    public const uint CoInitMultithreaded = 0x0;

    // ── Media Foundation ─────────────────────────────────────────────────────────────────────────────────────────
    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport("mfplat", ExactSpelling = true)]
    public static extern int MFCreateMediaType(IntPtr* mediaType);

    [DllImport("mfreadwrite", ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, IntPtr* reader);

    // ── COM helpers ──────────────────────────────────────────────────────────────────────────────────────────────
    // Function pointer for vtable slot `index` of COM object `obj` (slot = index * 8 on x64).
    public static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) ((delegate* unmanaged<IntPtr, uint>)Slot(obj, 2))(obj);
    }
}

// WAVEFORMATEX — Pack=1 (18 bytes), as declared in mmreg.h.
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatEx
{
    public ushort FormatTag;
    public ushort Channels;
    public uint   SamplesPerSec;
    public uint   AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort CbSize;
}

// DSCBUFFERDESC (x64, dwSize 0x28): dwSize@0 dwFlags@4 dwBufferBytes@8 dwReserved@0xC lpwfxFormat@0x10
// dwFXCount@0x18 lpDSCFXDesc@0x20 — offsets pinned explicitly so the layout can't drift with packing rules.
[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal unsafe struct DscBufferDesc
{
    [FieldOffset(0x00)] public uint          Size;
    [FieldOffset(0x04)] public uint          Flags;
    [FieldOffset(0x08)] public uint          BufferBytes;
    [FieldOffset(0x0C)] public uint          Reserved;
    [FieldOffset(0x10)] public WaveFormatEx* Format;
    [FieldOffset(0x18)] public uint          FxCount;
    [FieldOffset(0x20)] public IntPtr        FxDesc;
}
